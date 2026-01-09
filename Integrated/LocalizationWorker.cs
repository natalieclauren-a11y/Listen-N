using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Localization.ML;
using CnLocalizationRequest = Integrated.Contracts.LocalizationRequest;
using RtLocalizationPrediction = Integrated.Runtime.LocalizationPrediction;
using RtLocalizationRequest = Integrated.Runtime.LocalizationRequest;

namespace Integrated.Runtime
{
    public interface ILocalizer
    {
        RtLocalizationPrediction Predict(RtLocalizationRequest request, CancellationToken cancellationToken);
    }

    public sealed class LocalizationWorker
    {
        private readonly Channel<RtLocalizationRequest> _channel;
        private readonly ILocalizer _localizer;
        private readonly object _enqueueLock = new();
        private readonly int _capacity;
        private readonly LocalizationWorkerOptions _options;
        private long _cooldownUntilTicks;
        private Task? _runTask;
        private int _started;

        public LocalizationWorker(string artifactsDirectory, int capacity = 4, LocalizationWorkerOptions? options = null)
            : this(new PipelineLocalizer(artifactsDirectory), capacity, options)
        {
        }

        public LocalizationWorker(LocalizationPipeline pipeline, int capacity = 4, LocalizationWorkerOptions? options = null)
            : this(new PipelineLocalizer(pipeline), capacity, options)
        {
        }

        public LocalizationWorker(ILocalizer localizer, int capacity = 4, LocalizationWorkerOptions? options = null)
        {
            if (capacity <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(capacity), "Capacity must be positive.");
            }

            _localizer = localizer ?? throw new ArgumentNullException(nameof(localizer));
            _capacity = capacity;
            _options = options ?? new LocalizationWorkerOptions();
            _channel = Channel.CreateBounded<RtLocalizationRequest>(new BoundedChannelOptions(capacity)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait
            });
        }

        public event Action<RtLocalizationRequest, RtLocalizationPrediction>? OnResult;

        public Task Start(CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref _started, 1) == 1)
            {
                return _runTask ?? Task.CompletedTask;
            }

            _runTask = Task.Run(() => RunAsync(cancellationToken), CancellationToken.None);
            return _runTask;
        }

        public void Enqueue(RtLocalizationRequest request)
        {
            if (!request.IsManual && IsInCooldown())
            {
                return;
            }

            if (_channel.Writer.TryWrite(request))
            {
                return;
            }

            if (_channel.Reader.Completion.IsCompleted)
            {
                return;
            }

            lock (_enqueueLock)
            {
                if (_channel.Writer.TryWrite(request))
                {
                    return;
                }

                if (_channel.Reader.Completion.IsCompleted)
                {
                    return;
                }

                var pending = new List<RtLocalizationRequest>();
                while (_channel.Reader.TryRead(out var existing))
                {
                    pending.Add(existing);
                }

                if (request.IsProbe)
                {
                    if (!TryInsertProbe(pending, request))
                    {
                        Requeue(pending);
                        return;
                    }
                }
                else
                {
                    DropOlderRequests(pending, request, includeFinals: true);
                    EnsureRoomForFinal(pending);
                    pending.Add(request);
                }

                Requeue(pending);
            }
        }

        private async Task RunAsync(CancellationToken cancellationToken)
        {
            try
            {
                await foreach (var request in _channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                {
                    var prediction = ExecutePredict(request, cancellationToken);
                    if (ShouldEnterCooldown(prediction.Reason))
                    {
                        EnterCooldown();
                    }

                    OnResult?.Invoke(request, prediction);
                }
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                _channel.Writer.TryComplete();
            }
        }

        private RtLocalizationPrediction ExecutePredict(RtLocalizationRequest request, CancellationToken workerToken)
        {
            CancellationTokenSource? timeoutCts = null;
            CancellationTokenSource? linkedCts = null;
            try
            {
                var tokens = new List<CancellationToken> { workerToken, request.CancellationToken };
                if (_options.InferenceTimeoutMs.HasValue && _options.InferenceTimeoutMs.Value > 0)
                {
                    timeoutCts = new CancellationTokenSource(_options.InferenceTimeoutMs.Value);
                    tokens.Add(timeoutCts.Token);
                }

                linkedCts = CancellationTokenSource.CreateLinkedTokenSource(tokens.ToArray());
                return _localizer.Predict(request, linkedCts.Token);
            }
            catch (OperationCanceledException)
            {
                if (timeoutCts?.IsCancellationRequested == true)
                {
                    return CreateRefusal("Timeout");
                }

                if (workerToken.IsCancellationRequested || request.CancellationToken.IsCancellationRequested)
                {
                    return CreateRefusal("Cancelled");
                }

                return CreateRefusal("Cancelled");
            }
            catch (Exception ex)
            {
                return CreateRefusal(IsArtifactsNotLoaded(ex) ? "ArtifactsNotLoaded" : "MlException");
            }
            finally
            {
                linkedCts?.Dispose();
                timeoutCts?.Dispose();
            }
        }

        private RtLocalizationPrediction CreateRefusal(string reason)
        {
            return new RtLocalizationPrediction
            {
                IsOutOfDistribution = false,
                MahalanobisDistance = 0.0,
                ClassifierProbability = double.NaN,
                Label = "Unknown",
                PredictedVector = Array.Empty<double>(),
                Reason = reason
            };
        }

        private bool IsInCooldown()
        {
            if (_options.CooldownAfterFailureMs <= 0)
            {
                return false;
            }

            var cooldownUntil = Interlocked.Read(ref _cooldownUntilTicks);
            return cooldownUntil > DateTimeOffset.UtcNow.UtcTicks;
        }

        private void EnterCooldown()
        {
            if (_options.CooldownAfterFailureMs <= 0)
            {
                return;
            }

            var cooldownUntil = DateTimeOffset.UtcNow.AddMilliseconds(_options.CooldownAfterFailureMs).UtcTicks;
            Interlocked.Exchange(ref _cooldownUntilTicks, cooldownUntil);
        }

        private static bool ShouldEnterCooldown(string? reason)
        {
            return reason is "MlException" or "Timeout" or "ArtifactsNotLoaded";
        }

        private static bool IsArtifactsNotLoaded(Exception ex)
        {
            return ex is LocalizationArtifactsException
                || ex is System.IO.FileNotFoundException
                || ex is System.IO.DirectoryNotFoundException;
        }

        private void Requeue(List<RtLocalizationRequest> pending)
        {
            foreach (var item in pending)
            {
                _channel.Writer.TryWrite(item);
            }
        }

        private bool TryInsertProbe(List<RtLocalizationRequest> pending, RtLocalizationRequest request)
        {
            DropOlderRequests(pending, request, includeFinals: false);
            if (pending.Count < _capacity)
            {
                pending.Add(request);
                return true;
            }

            int dropIndex = FindOldestProbeIndex(pending);
            if (dropIndex < 0)
            {
                return false;
            }

            pending.RemoveAt(dropIndex);
            pending.Add(request);
            return true;
        }

        private void EnsureRoomForFinal(List<RtLocalizationRequest> pending)
        {
            if (pending.Count < _capacity)
            {
                return;
            }

            int dropIndex = FindOldestProbeIndex(pending);
            if (dropIndex < 0 && pending.Count > 0)
            {
                dropIndex = FindOldestIndex(pending);
            }

            if (dropIndex >= 0)
            {
                pending.RemoveAt(dropIndex);
            }
        }

        private static int FindOldestProbeIndex(List<RtLocalizationRequest> pending)
        {
            int index = -1;
            long oldest = long.MaxValue;
            for (int i = 0; i < pending.Count; i++)
            {
                if (pending[i].IsProbe)
                {
                    long sequence = pending[i].Sequence;
                    if (sequence < oldest)
                    {
                        oldest = sequence;
                        index = i;
                    }
                }
            }

            return index;
        }

        private static int FindOldestIndex(List<RtLocalizationRequest> pending)
        {
            int index = -1;
            long oldest = long.MaxValue;
            for (int i = 0; i < pending.Count; i++)
            {
                long sequence = pending[i].Sequence;
                if (sequence < oldest)
                {
                    oldest = sequence;
                    index = i;
                }
            }

            return index;
        }

        private static void DropOlderRequests(List<RtLocalizationRequest> pending, RtLocalizationRequest? request, bool includeFinals)
        {
            if (pending.Count == 0)
            {
                return;
            }

            long sequence = request?.Sequence ?? long.MaxValue;
            for (int i = pending.Count - 1; i >= 0; i--)
            {
                var existing = pending[i];
                if (request is null)
                {
                    if (includeFinals && !existing.IsProbe && existing.Sequence < sequence)
                    {
                        pending.RemoveAt(i);
                    }
                    continue;
                }

                if (existing.IsProbe != request.IsProbe)
                {
                    continue;
                }

                if (existing.Sequence <= sequence)
                {
                    pending.RemoveAt(i);
                }
            }
        }

        private sealed class PipelineLocalizer : ILocalizer
        {
            private readonly LocalizationPipeline _pipeline;

            public PipelineLocalizer(string artifactsDirectory)
            {
                if (string.IsNullOrWhiteSpace(artifactsDirectory))
                {
                    throw new ArgumentException("Artifacts directory is required.", nameof(artifactsDirectory));
                }

                _pipeline = LocalizationPipeline.Load(artifactsDirectory);
            }

            public PipelineLocalizer(LocalizationPipeline pipeline)
            {
                _pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
            }

            public RtLocalizationPrediction Predict(RtLocalizationRequest request, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var row = new Localization.ML.LocalizationRow
                {
                    Channels = request.Row.Channels.Select(value => value).ToArray(),
                    DurationSeconds = request.Row.DurationSeconds
                };

                var prediction = _pipeline.Predict(row);
                return new LocalizationPrediction
                {
                    IsOutOfDistribution = prediction.Diagnostics.IsOutOfDistribution,
                    MahalanobisDistance = prediction.Diagnostics.MahalanobisDistance,
                    ClassifierProbability = prediction.Probability,
                    Label = prediction.Label,
                    PredictedVector = prediction.Coordinates.ToArray()
                };
            }
        }
    }
}
