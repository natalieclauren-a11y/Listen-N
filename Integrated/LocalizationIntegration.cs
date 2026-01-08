using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Integrated.Contracts;
using CnLocalizationRequest = Integrated.Contracts.LocalizationRequest;
using ContractsAnalysisSnapshot = Integrated.Contracts.AnalysisSnapshot;
using ContractsLocalizationResult = Integrated.Contracts.LocalizationResult;
using ContractsLocalizationRow = Integrated.Contracts.LocalizationRow;
using ContractsLocalizationTriggerSource = Integrated.Contracts.LocalizationTriggerSource;
using RtLocalizationPrediction = Integrated.Runtime.LocalizationPrediction;
using RtLocalizationRequest = Integrated.Runtime.LocalizationRequest;

namespace Integrated.Runtime
{
    public interface ILocalizationPipeline
    {
        Task<ContractsLocalizationResult> RunAsync(ContractsLocalizationRow row, CancellationToken cancellationToken);
    }

    public sealed class LocalizationChannelBridge
    {
        public LocalizationChannelBridge(Channel<ContractsAnalysisSnapshot> snapshots, Channel<CnLocalizationRequest> requests)
        {
            Snapshots = snapshots;
            Requests = requests;
        }

        public Channel<ContractsAnalysisSnapshot> Snapshots { get; }
        public Channel<CnLocalizationRequest> Requests { get; }

        public static LocalizationChannelBridge Create(int snapshotCapacity = 1, int requestCapacity = 4)
        {
            var snapshots = Channel.CreateBounded<ContractsAnalysisSnapshot>(new BoundedChannelOptions(snapshotCapacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = false,
                SingleWriter = false
            });

            var requests = Channel.CreateBounded<CnLocalizationRequest>(new BoundedChannelOptions(requestCapacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false
            });

            return new LocalizationChannelBridge(snapshots, requests);
        }
    }

    public sealed class RealtimePublisherService
    {
        private readonly ChannelWriter<AnalysisSnapshot> _writer;

        public RealtimePublisherService(ChannelWriter<AnalysisSnapshot> writer)
        {
            _writer = writer;
        }

        public Task PublishAsync(AnalysisSnapshot snapshot, CancellationToken cancellationToken = default)
        {
            return _writer.WriteAsync(snapshot, cancellationToken).AsTask();
        }
    }

    public sealed class LocalizationOrchestratorService : ILocalizationTrigger
    {
        private readonly ChannelReader<ContractsAnalysisSnapshot> _snapshotReader;
        private readonly Channel<CnLocalizationRequest> _requestChannel;
        private readonly ILocalizationTriggerPolicy _policy;
        private readonly ILocalizationPipeline _pipeline;
        private readonly LocalizationEpisodePolicy _episodePolicy;
        private readonly Channel<RtLocalizationRequest> _workerChannel;

        private readonly object _snapshotLock = new();
        private AnalysisSnapshot? _latestSnapshot;
        private Guid? _pendingAutoSnapshotId;

        public LocalizationOrchestratorService(
            ChannelReader<ContractsAnalysisSnapshot> snapshotReader,
            Channel<CnLocalizationRequest> requestChannel,
            ILocalizationTriggerPolicy policy,
            ILocalizationPipeline pipeline)
            : this(
                snapshotReader,
                requestChannel,
                policy,
                pipeline,
                new LocalizationEpisodePolicy(),
                CreateWorkerChannel())
        {
        }

        public LocalizationOrchestratorService(
            ChannelReader<ContractsAnalysisSnapshot> snapshotReader,
            Channel<CnLocalizationRequest> requestChannel,
            ILocalizationTriggerPolicy policy,
            ILocalizationPipeline pipeline,
            LocalizationEpisodePolicy episodePolicy,
            Channel<RtLocalizationRequest> workerChannel)
        {
            _snapshotReader = snapshotReader;
            _requestChannel = requestChannel;
            _policy = policy;
            _pipeline = pipeline;
            _episodePolicy = episodePolicy;
            _workerChannel = workerChannel;
        }

        public Task RunAsync(CancellationToken cancellationToken)
        {
            var consumeSnapshots = ConsumeSnapshotsAsync(cancellationToken);
            var processRequests = ProcessRequestsAsync(cancellationToken);
            return Task.WhenAll(consumeSnapshots, processRequests);
        }

        public async Task<RtLocalizationRequest> TriggerNowAsync(string reason, IDictionary<string, string>? tags = null)
        {
            var request = _episodePolicy.BuildManualProbeRequest();

            AnalysisSnapshot? snapshot = null;
            for (var attempt = 0; attempt < 50; attempt++)
            {
                lock (_snapshotLock)
                {
                    snapshot = _latestSnapshot;
                }

                if (snapshot is not null)
                {
                    break;
                }

                await Task.Delay(5).ConfigureAwait(false);
            }

            if (snapshot is null)
            {
                return request;
            }

            var contractRequest = new CnLocalizationRequest
            {
                TriggerReason = reason,
                TriggerSource = ContractsLocalizationTriggerSource.Manual,
                AllowPolicyBypass = true,
                Metadata = tags,
                RequestedSnapshotId = snapshot.SnapshotId
            };

            EnqueueRequest(contractRequest);
            return request;
        }

        private async Task ConsumeSnapshotsAsync(CancellationToken cancellationToken)
        {
            await foreach (var snapshot in _snapshotReader.ReadAllAsync(cancellationToken))
            {
                lock (_snapshotLock)
                {
                    _latestSnapshot = snapshot;
                }

                if (_policy.ShouldTrigger(snapshot))
                {
                    TryEnqueueAuto(snapshot);
                }
            }
        }

        private void TryEnqueueAuto(ContractsAnalysisSnapshot snapshot)
        {
            if (_pendingAutoSnapshotId.HasValue && _pendingAutoSnapshotId.Value == snapshot.SnapshotId)
            {
                return;
            }

            _pendingAutoSnapshotId = snapshot.SnapshotId;

            var request = new CnLocalizationRequest
            {
                TriggerReason = "AutoPolicy",
                TriggerSource = ContractsLocalizationTriggerSource.Auto,
                AllowPolicyBypass = false,
                RequestedSnapshotId = snapshot.SnapshotId
            };

            EnqueueRequest(request);
        }

        private async Task ProcessRequestsAsync(CancellationToken cancellationToken)
        {
            while (await _requestChannel.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                while (_requestChannel.Reader.TryRead(out var request))
                {
                    ContractsAnalysisSnapshot? snapshot = ResolveSnapshot(request);
                    if (snapshot is null)
                    {
                        continue;
                    }

                    var row = ContractsLocalizationRow.FromSnapshot(snapshot);
                    var result = await _pipeline.RunAsync(row, cancellationToken).ConfigureAwait(false);

                    var enriched = result with
                    {
                        SnapshotId = snapshot.SnapshotId,
                        TimestampUtc = snapshot.TimestampUtc,
                        TriggerSource = request.TriggerSource,
                        TriggerReason = request.TriggerReason,
                        Forced = request.AllowPolicyBypass,
                        Diagnostics = MergeDiagnostics(snapshot.Diagnostics, request.Metadata, result.Diagnostics)
                    };

                    _pendingAutoSnapshotId = null;
                    // Publish result hook point.
                    _ = enriched;
                }
            }
        }

        private ContractsAnalysisSnapshot? ResolveSnapshot(CnLocalizationRequest request)
        {
            lock (_snapshotLock)
            {
                if (_latestSnapshot is null)
                {
                    return null;
                }

                if (request.RequestedSnapshotId.HasValue && _latestSnapshot.SnapshotId != request.RequestedSnapshotId.Value)
                {
                    return null;
                }

                return _latestSnapshot;
            }
        }

        private void EnqueueRequest(CnLocalizationRequest request)
        {
            const int maxAttempts = 8;

            for (var attempt = 0; attempt < maxAttempts; attempt++)
            {
                if (_requestChannel.Writer.TryWrite(request))
                {
                    return;
                }

                if (_requestChannel.Reader.Completion.IsCompleted)
                {
                    return;
                }

                _requestChannel.Reader.TryRead(out _);
            }
        }

        private void EnqueueWorkerRequest(RtLocalizationRequest request)
        {
            const int maxAttempts = 8;

            for (var attempt = 0; attempt < maxAttempts; attempt++)
            {
                if (_workerChannel.Writer.TryWrite(request))
                {
                    return;
                }

                if (_workerChannel.Reader.Completion.IsCompleted)
                {
                    return;
                }

                _workerChannel.Reader.TryRead(out _);
            }
        }

        private static Channel<RtLocalizationRequest> CreateWorkerChannel(int capacity = 4)
        {
            return Channel.CreateBounded<RtLocalizationRequest>(new BoundedChannelOptions(capacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = false,
                SingleWriter = false
            });
        }

        private static IDictionary<string, string>? MergeDiagnostics(
            IDictionary<string, string>? snapshotDiagnostics,
            IDictionary<string, string>? requestMetadata,
            IDictionary<string, string>? resultDiagnostics)
        {
            if (snapshotDiagnostics is null && requestMetadata is null && resultDiagnostics is null)
            {
                return null;
            }

            var merged = new Dictionary<string, string>();
            void AddRange(IDictionary<string, string>? values)
            {
                if (values is null)
                {
                    return;
                }

                foreach (var kvp in values)
                {
                    merged[kvp.Key] = kvp.Value;
                }
            }

            AddRange(snapshotDiagnostics);
            AddRange(requestMetadata);
            AddRange(resultDiagnostics);
            return merged;
        }
    }
}
