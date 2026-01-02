using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Integrated.Contracts;

namespace Integrated.Runtime
{
    public interface ILocalizationPipeline
    {
        Task<LocalizationResult> RunAsync(LocalizationRow row, CancellationToken cancellationToken);
    }

    public sealed class LocalizationChannelBridge
    {
        public LocalizationChannelBridge(Channel<AnalysisSnapshot> snapshots, Channel<LocalizationRequest> requests)
        {
            Snapshots = snapshots;
            Requests = requests;
        }

        public Channel<AnalysisSnapshot> Snapshots { get; }
        public Channel<LocalizationRequest> Requests { get; }

        public static LocalizationChannelBridge Create(int snapshotCapacity = 1, int requestCapacity = 4)
        {
            var snapshots = Channel.CreateBounded<AnalysisSnapshot>(new BoundedChannelOptions(snapshotCapacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = false,
                SingleWriter = false
            });

            var requests = Channel.CreateBounded<LocalizationRequest>(new BoundedChannelOptions(requestCapacity)
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
        private readonly ChannelReader<AnalysisSnapshot> _snapshotReader;
        private readonly Channel<LocalizationRequest> _requestChannel;
        private readonly ILocalizationTriggerPolicy _policy;
        private readonly ILocalizationPipeline _pipeline;

        private readonly object _snapshotLock = new();
        private AnalysisSnapshot? _latestSnapshot;
        private Guid? _pendingAutoSnapshotId;

        public LocalizationOrchestratorService(
            ChannelReader<AnalysisSnapshot> snapshotReader,
            Channel<LocalizationRequest> requestChannel,
            ILocalizationTriggerPolicy policy,
            ILocalizationPipeline pipeline)
        {
            _snapshotReader = snapshotReader;
            _requestChannel = requestChannel;
            _policy = policy;
            _pipeline = pipeline;
        }

        public Task RunAsync(CancellationToken cancellationToken)
        {
            var consumeSnapshots = ConsumeSnapshotsAsync(cancellationToken);
            var processRequests = ProcessRequestsAsync(cancellationToken);
            return Task.WhenAll(consumeSnapshots, processRequests);
        }

        public Task<LocalizationRequest> TriggerNowAsync(string reason, IDictionary<string, string>? metadata = null)
        {
            var request = new LocalizationRequest
            {
                TriggerReason = reason,
                TriggerSource = LocalizationTriggerSource.Manual,
                AllowPolicyBypass = true,
                Metadata = metadata
            };

            EnqueueRequest(request);
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

        private void TryEnqueueAuto(AnalysisSnapshot snapshot)
        {
            if (_pendingAutoSnapshotId.HasValue && _pendingAutoSnapshotId.Value == snapshot.SnapshotId)
            {
                return;
            }

            _pendingAutoSnapshotId = snapshot.SnapshotId;

            var request = new LocalizationRequest
            {
                TriggerReason = "AutoPolicy",
                TriggerSource = LocalizationTriggerSource.Auto,
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
                    AnalysisSnapshot? snapshot = ResolveSnapshot(request);
                    if (snapshot is null)
                    {
                        continue;
                    }

                    var row = LocalizationRow.FromSnapshot(snapshot);
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

        private AnalysisSnapshot? ResolveSnapshot(LocalizationRequest request)
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

        private void EnqueueRequest(LocalizationRequest request)
        {
            while (!_requestChannel.Writer.TryWrite(request))
            {
                _requestChannel.Reader.TryRead(out _);
            }
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
