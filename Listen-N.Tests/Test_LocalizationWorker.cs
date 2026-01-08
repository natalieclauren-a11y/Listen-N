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

            worker.Enqueue(CreateRequest(isProbe: true));

            var enqueueTask = Task.Run(() => worker.Enqueue(CreateRequest(isProbe: true)));

            var completed = await Task.WhenAny(enqueueTask, Task.Delay(TimeSpan.FromSeconds(1)));
            Assert.Equal(enqueueTask, completed);
        }

        [Fact]
        public async Task ProbeCoalescingDropsOldestProbe()
        {
            var localizer = new FakeLocalizer();
            var worker = new LocalizationWorker(localizer, capacity: 2);

            var probe1 = CreateRequest(isProbe: true);
            var probe2 = CreateRequest(isProbe: true);
            var probe3 = CreateRequest(isProbe: true);

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

            var probe1 = CreateRequest(isProbe: true);
            var probe2 = CreateRequest(isProbe: true);
            var finalRequest = CreateRequest(isProbe: false);

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

        private static RtLocalizationRequest CreateRequest(bool isProbe)
        {
            return new RtLocalizationRequest
            {
                EpisodeId = Guid.NewGuid(),
                IsProbe = isProbe,
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

            public RtLocalizationPrediction Predict(RtLocalizationRequest request)
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
    }
}
