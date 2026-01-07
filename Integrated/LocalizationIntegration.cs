using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Integrated.Contracts;
using ContractsAnalysisSnapshot = Integrated.Contracts.AnalysisSnapshot;
using ContractsLocalizationRequest = Integrated.Contracts.LocalizationRequest;
using ContractsLocalizationResult = Integrated.Contracts.LocalizationResult;
using ContractsLocalizationRow = Integrated.Contracts.LocalizationRow;
using ContractsLocalizationTriggerSource = Integrated.Contracts.LocalizationTriggerSource;

namespace Integrated.Runtime
{
    public interface ILocalizationPipeline
    {
        Task<ContractsLocalizationResult> RunAsync(ContractsLocalizationRow row, CancellationToken cancellationToken);
    }

    public sealed class LocalizationChannelBridge
    {
        public LocalizationChannelBridge(Channel<ContractsAnalysisSnapshot> snapshots, Channel<ContractsLocalizationRequest> requests)
        {
            Snapshots = snapshots;
            Requests = requests;
        }

        public Channel<ContractsAnalysisSnapshot> Snapshots { get; }
        public Channel<ContractsLocalizationRequest> Requests { get; }

        public static LocalizationChannelBridge Create(int snapshotCapacity = 1, int requestCapacity = 4)
        {
            var snapshots = Channel.CreateBounded<ContractsAnalysisSnapshot>(new BoundedChannelOptions(snapshotCapacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = false,
                SingleWriter = false
            });

            var requests = Channel.CreateBounded<ContractsLocalizationRequest>(new BoundedChannelOptions(requestCapacity)
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
        private readonly Channel<ContractsLocalizationRequest> _requestChannel;
        private readonly ILocalizationTriggerPolicy _policy;
        private readonly ILocalizationPipeline _pipeline;
        private readonly LocalizationEpisodePolicy _episodePolicy;
        private readonly Channel<LocalizationRequest> _workerChannel;

        private readonly object _snapshotLock = new();
        private AnalysisSnapshot? _latestSnapshot;
        private Guid? _pendingAutoSnapshotId;

        public LocalizationOrchestratorService(
            ChannelReader<ContractsAnalysisSnapshot> snapshotReader,
            Channel<ContractsLocalizationRequest> requestChannel,
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
            Channel<ContractsLocalizationRequest> requestChannel,
            ILocalizationTriggerPolicy policy,
            ILocalizationPipeline pipeline,
            LocalizationEpisodePolicy episodePolicy,
            Channel<LocalizationRequest> workerChannel)
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

        public Task<LocalizationRequest> TriggerNowAsync(string reason, IDictionary<string, string>? tags = null)
        {
            var request = _episodePolicy.BuildManualProbeRequest();
            

            var contractRequest = new ContractsLocalizationRequest
            {
                TriggerReason = reason,
                TriggerSource = ContractsLocalizationTriggerSource.Manual,
                AllowPolicyBypass = true,
                Metadata = tags
            };

            EnqueueRequest(contractRequest);
            return Task.FromResult(request);
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

            var request = new ContractsLocalizationRequest
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

        private ContractsAnalysisSnapshot? ResolveSnapshot(ContractsLocalizationRequest request)
        {
            lock (_snapshotLock)
            {
                if (_latestSnapshot is null)
                {
                    return null;
                }

                if (request.RequestedSnapshotId.HasValue && _latestSnapshot.SnapshotId != request.RequestedSnapshotId.Value)
                {
                    return _latestSnapshot;
                }

                return _latestSnapshot;
            }
        }

        private void EnqueueRequest(ContractsLocalizationRequest request)
        {
            while (!_requestChannel.Writer.TryWrite(request))
            {
                _requestChannel.Reader.TryRead(out _);
            }
        }

        private void EnqueueWorkerRequest(LocalizationRequest request)
        {
            while (!_workerChannel.Writer.TryWrite(request))
            {
                _workerChannel.Reader.TryRead(out _);
            }
        }

        private static Channel<LocalizationRequest> CreateWorkerChannel(int capacity = 4)
        {
            return Channel.CreateBounded<LocalizationRequest>(new BoundedChannelOptions(capacity)
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
