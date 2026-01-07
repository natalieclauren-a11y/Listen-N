using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Localization.ML;

namespace Integrated.Runtime
{
    public interface ILocalizer
    {
        LocalizationPrediction Predict(LocalizationRequest request);
    }

    public sealed class LocalizationWorker
    {
        private readonly Channel<LocalizationRequest> _channel;
        private readonly ILocalizer _localizer;
        private readonly object _enqueueLock = new();
        private readonly int _capacity;
        private Task? _runTask;
        private int _started;

        public LocalizationWorker(string artifactsDirectory, int capacity = 4)
            : this(new PipelineLocalizer(artifactsDirectory), capacity)
        {
        }

        public LocalizationWorker(ILocalizer localizer, int capacity = 4)
        {
            if (capacity <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(capacity), "Capacity must be positive.");
            }

            _localizer = localizer ?? throw new ArgumentNullException(nameof(localizer));
            _capacity = capacity;
            _channel = Channel.CreateBounded<LocalizationRequest>(new BoundedChannelOptions(capacity)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait
            });
        }

        public event Action<LocalizationRequest, LocalizationPrediction>? OnResult;

        public Task Start(CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref _started, 1) == 1)
            {
                return _runTask ?? Task.CompletedTask;
            }

            _runTask = Task.Run(() => RunAsync(cancellationToken), CancellationToken.None);
            return _runTask;
        }

        public void Enqueue(LocalizationRequest request)
        {
            if (_channel.Writer.TryWrite(request))
            {
                return;
            }

            lock (_enqueueLock)
            {
                if (_channel.Writer.TryWrite(request))
                {
                    return;
                }

                var pending = new List<LocalizationRequest>();
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
                    var prediction = _localizer.Predict(request);
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

        private void Requeue(List<LocalizationRequest> pending)
        {
            foreach (var item in pending)
            {
                _channel.Writer.TryWrite(item);
            }
        }

        private bool TryInsertProbe(List<LocalizationRequest> pending, LocalizationRequest request)
        {
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

        private void EnsureRoomForFinal(List<LocalizationRequest> pending)
        {
            if (pending.Count < _capacity)
            {
                return;
            }

            int dropIndex = FindOldestProbeIndex(pending);
            if (dropIndex < 0 && pending.Count > 0)
            {
                dropIndex = 0;
            }

            if (dropIndex >= 0)
            {
                pending.RemoveAt(dropIndex);
            }
        }

        private static int FindOldestProbeIndex(List<LocalizationRequest> pending)
        {
            for (int i = 0; i < pending.Count; i++)
            {
                if (pending[i].IsProbe)
                {
                    return i;
                }
            }

            return -1;
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

            public LocalizationPrediction Predict(LocalizationRequest request)
            {
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
