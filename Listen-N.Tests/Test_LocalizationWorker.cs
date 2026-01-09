using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Integrated.Runtime;
using CnLocalizationRequest = Integrated.Contracts.LocalizationRequest;
using RtILocalizer = Integrated.Runtime.ILocalizer;
using RtLocalizationPrediction = Integrated.Runtime.LocalizationPrediction;
using RtLocalizationRequest = Integrated.Runtime.LocalizationRequest;
using Xunit;

namespace Listen_N.Tests
{
    public sealed class LocalizationWorkerTests
    {
        [Fact]
        public async Task EnqueueDoesNotBlockWhenQueueIsFull()
        {
            var localizer = new FakeLocalizer();
            var worker = new LocalizationWorker(localizer, capacity: 1);

            worker.Enqueue(CreateRequest(isProbe: true, sequence: 1));

            var enqueueTask = Task.Run(() => worker.Enqueue(CreateRequest(isProbe: true, sequence: 2)));

            var completed = await Task.WhenAny(enqueueTask, Task.Delay(TimeSpan.FromSeconds(1)));
            Assert.Equal(enqueueTask, completed);
        }

        [Fact]
        public async Task ProbeCoalescingDropsOldestProbe()
        {
            var localizer = new FakeLocalizer();
            var worker = new LocalizationWorker(localizer, capacity: 2);

            var probe1 = CreateRequest(isProbe: true, sequence: 1);
            var probe2 = CreateRequest(isProbe: true, sequence: 2);
            var probe3 = CreateRequest(isProbe: true, sequence: 3);

            worker.Enqueue(probe1);
            worker.Enqueue(probe2);
            worker.Enqueue(probe3);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var runTask = worker.Start(cts.Token);

            await AwaitProcessedAsync(localizer, expectedCount: 2);
            cts.Cancel();
            await AwaitStopAsync(runTask);

            var processed = localizer.ProcessedRequests.ToArray();
            Assert.DoesNotContain(processed, req => req.EpisodeId == probe1.EpisodeId);
            Assert.Contains(processed, req => req.EpisodeId == probe2.EpisodeId);
            Assert.Contains(processed, req => req.EpisodeId == probe3.EpisodeId);
        }

        [Fact]
        public async Task FinalRequestIsPrioritizedOverPendingProbe()
        {
            var localizer = new FakeLocalizer();
            var worker = new LocalizationWorker(localizer, capacity: 2);

            var probe1 = CreateRequest(isProbe: true, sequence: 1);
            var probe2 = CreateRequest(isProbe: true, sequence: 2);
            var finalRequest = CreateRequest(isProbe: false, sequence: 3);

            worker.Enqueue(probe1);
            worker.Enqueue(probe2);
            worker.Enqueue(finalRequest);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var runTask = worker.Start(cts.Token);

            await AwaitProcessedAsync(localizer, expectedCount: 2);
            cts.Cancel();
            await AwaitStopAsync(runTask);

            var processed = localizer.ProcessedRequests.ToArray();
            Assert.DoesNotContain(processed, req => req.EpisodeId == probe1.EpisodeId);
            Assert.Contains(processed, req => req.EpisodeId == probe2.EpisodeId);
            Assert.Contains(processed, req => req.EpisodeId == finalRequest.EpisodeId);
        }

        [Fact]
        public async Task WorkerConvertsExceptionIntoRefusalAndContinues()
        {
            var localizer = new ThrowingLocalizer();
            var worker = new LocalizationWorker(localizer, capacity: 1, options: new LocalizationWorkerOptions
            {
                CooldownAfterFailureMs = 0
            });

            var firstResult = new TaskCompletionSource<RtLocalizationPrediction>(TaskCreationOptions.RunContinuationsAsynchronously);
            var secondResult = new TaskCompletionSource<RtLocalizationPrediction>(TaskCreationOptions.RunContinuationsAsynchronously);
            int resultCount = 0;
            worker.OnResult += (_, pred) =>
            {
                var current = Interlocked.Increment(ref resultCount);
                if (current == 1)
                {
                    firstResult.TrySetResult(pred);
                }
                else if (current == 2)
                {
                    secondResult.TrySetResult(pred);
                }
            };

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var runTask = worker.Start(cts.Token);

            worker.Enqueue(CreateRequest(isProbe: true, sequence: 1));

            var completed = await Task.WhenAny(firstResult.Task, Task.Delay(500));
            Assert.Equal(firstResult.Task, completed);
            var first = await firstResult.Task;
            Assert.False(runTask.IsCompleted);
            Assert.Equal("Unknown", first.Label);
            Assert.Equal("MlException", first.Reason);

            worker.Enqueue(CreateRequest(isProbe: true, sequence: 2));
            completed = await Task.WhenAny(secondResult.Task, Task.Delay(500));
            Assert.Equal(secondResult.Task, completed);
            var second = await secondResult.Task;
            Assert.False(runTask.IsCompleted);
            Assert.Equal("MlException", second.Reason);

            cts.Cancel();
            await AwaitStopAsync(runTask);
        }

        [Fact]
        public async Task CooldownSuppressesRepeatedTriggers()
        {
            var localizer = new FlakyLocalizer();
            var worker = new LocalizationWorker(localizer, capacity: 1, options: new LocalizationWorkerOptions
            {
                CooldownAfterFailureMs = 2000
            });

            var results = new ConcurrentQueue<RtLocalizationPrediction>();
            worker.OnResult += (_, pred) => results.Enqueue(pred);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var runTask = worker.Start(cts.Token);

            worker.Enqueue(CreateRequest(isProbe: true, sequence: 1));
            await AwaitResultCountAsync(results, expectedCount: 1);
            Assert.Equal(1, localizer.CallCount);

            worker.Enqueue(CreateRequest(isProbe: true, sequence: 2));
            worker.Enqueue(CreateRequest(isProbe: true, sequence: 3));
            worker.Enqueue(CreateRequest(isProbe: true, sequence: 4));

            await Task.Delay(100);
            Assert.Equal(1, localizer.CallCount);
            Assert.Equal(1, results.Count);

            worker.Enqueue(CreateRequest(isProbe: true, isManual: true, sequence: 5));
            await AwaitResultCountAsync(results, expectedCount: 2);
            Assert.Equal(2, localizer.CallCount);

            cts.Cancel();
            await AwaitStopAsync(runTask);
        }

        [Fact]
        public async Task QueueRemainsBoundedAndKeepsLatestSnapshot()
        {
            var localizer = new BlockingLocalizer();
            var worker = new LocalizationWorker(localizer, capacity: 1);
            var processed = new ConcurrentQueue<RtLocalizationRequest>();
            worker.OnResult += (req, _) => processed.Enqueue(req);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var runTask = worker.Start(cts.Token);

            worker.Enqueue(CreateRequest(isProbe: true, sequence: 1));
            Assert.True(localizer.WaitForStart(TimeSpan.FromSeconds(1)));

            for (int i = 2; i <= 11; i++)
            {
                worker.Enqueue(CreateRequest(isProbe: true, sequence: i));
            }

            localizer.Release();

            await AwaitResultCountAsync(processed, expectedCount: 2);
            cts.Cancel();
            await AwaitStopAsync(runTask);

            var processedArray = processed.ToArray();
            Assert.True(processedArray.Length <= 2);
            var lastSequence = processedArray.Last().Sequence;
            Assert.Equal(11, lastSequence);
        }

        private static async Task AwaitProcessedAsync(FakeLocalizer localizer, int expectedCount)
        {
            var timeout = DateTime.UtcNow.AddSeconds(1);
            while (DateTime.UtcNow < timeout)
            {
                if (localizer.ProcessedRequests.Count >= expectedCount)
                {
                    return;
                }

                await Task.Delay(10);
            }

            throw new TimeoutException($"Timed out waiting for {expectedCount} processed requests.");
        }

        private static async Task AwaitResultCountAsync<T>(ConcurrentQueue<T> results, int expectedCount)
        {
            var timeout = DateTime.UtcNow.AddSeconds(1);
            while (DateTime.UtcNow < timeout)
            {
                if (results.Count >= expectedCount)
                {
                    return;
                }

                await Task.Delay(10);
            }

            throw new TimeoutException($"Timed out waiting for {expectedCount} results.");
        }

        private static async Task AwaitStopAsync(Task runTask)
        {
            try
            {
                await runTask;
            }
            catch (OperationCanceledException)
            {
            }
        }

        private static RtLocalizationRequest CreateRequest(bool isProbe, bool isManual = false, long sequence = 0)
        {
            return new RtLocalizationRequest
            {
                EpisodeId = Guid.NewGuid(),
                IsProbe = isProbe,
                IsManual = isManual,
                Sequence = sequence == 0 ? DateTimeOffset.UtcNow.UtcTicks : sequence,
                CancellationToken = CancellationToken.None,
                Row = new LocalizationRow
                {
                    DurationSeconds = 1.0,
                    Channels = Enumerable.Repeat(1.0, 15).ToArray()
                },
                EpisodeStartUtc = DateTimeOffset.UtcNow,
                EpisodeCurrentEndUtc = DateTimeOffset.UtcNow
            };
        }

        private sealed class FakeLocalizer : RtILocalizer
        {
            public ConcurrentQueue<RtLocalizationRequest> ProcessedRequests { get; } = new();

            public RtLocalizationPrediction Predict(RtLocalizationRequest request, CancellationToken cancellationToken)
            {
                ProcessedRequests.Enqueue(request);
                return new RtLocalizationPrediction
                {
                    IsOutOfDistribution = false,
                    MahalanobisDistance = 0.0,
                    ClassifierProbability = 0.5,
                    Label = request.IsProbe ? "Probe" : "Final",
                    PredictedVector = new[] { 0.0, 0.0, 0.0 }
                };
            }
        }

        private sealed class ThrowingLocalizer : RtILocalizer
        {
            public RtLocalizationPrediction Predict(RtLocalizationRequest request, CancellationToken cancellationToken)
            {
                throw new InvalidOperationException("ML failure");
            }
        }

        private sealed class FlakyLocalizer : RtILocalizer
        {
            private int _callCount;

            public int CallCount => _callCount;

            public RtLocalizationPrediction Predict(RtLocalizationRequest request, CancellationToken cancellationToken)
            {
                var call = Interlocked.Increment(ref _callCount);
                if (call == 1)
                {
                    throw new InvalidOperationException("ML failure");
                }

                return new RtLocalizationPrediction
                {
                    IsOutOfDistribution = false,
                    MahalanobisDistance = 0.0,
                    ClassifierProbability = 0.9,
                    Label = "Recovered",
                    PredictedVector = new[] { 1.0, 1.0, 1.0 }
                };
            }
        }

        private sealed class BlockingLocalizer : RtILocalizer
        {
            private readonly ManualResetEventSlim _started = new(false);
            private readonly TaskCompletionSource<bool> _blocker = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private int _blockNext = 1;

            public bool WaitForStart(TimeSpan timeout) => _started.Wait(timeout);

            public void Release() => _blocker.TrySetResult(true);

            public RtLocalizationPrediction Predict(RtLocalizationRequest request, CancellationToken cancellationToken)
            {
                _started.Set();
                if (Interlocked.Exchange(ref _blockNext, 0) == 1)
                {
                    _blocker.Task.Wait(cancellationToken);
                }

                return new RtLocalizationPrediction
                {
                    IsOutOfDistribution = false,
                    MahalanobisDistance = 0.0,
                    ClassifierProbability = 0.5,
                    Label = request.IsProbe ? "Probe" : "Final",
                    PredictedVector = new[] { 0.0, 0.0, 0.0 }
                };
            }
        }
    }
}
