using System;
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
    public sealed class LocalizationRealtimeLoopTests
    {
        [Fact]
        public async Task PublishesWhenStable()
        {
            var policy = new LocalizationEpisodePolicy(new LocalizationEpisodePolicyConfig
            {
                ConfuseDebounceWindows = 1,
                RecoverDebounceWindows = 2,
                MinPublishDurationSeconds = 4.0,
                MaxPublishDurationSeconds = 20.0,
                CheckEveryCounts = 5000,
                StabilityK = 2,
                EarlyStopK = 2
            });

            var worker = new LocalizationWorker(new StableLocalizer(), capacity: 2);
            policy.OnRequestMl += worker.Enqueue;
            worker.OnResult += policy.OnMlResult;

            var publishTcs = new TaskCompletionSource<LocalizationPublishResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            policy.OnPublish += result => publishTcs.TrySetResult(result);

            using var cts = new CancellationTokenSource();
            worker.Start(cts.Token);

            var start = DateTimeOffset.UtcNow;
            for (int i = 0; i < 10 && !publishTcs.Task.IsCompleted; i++)
            {
                var summary = new RtWindowSummary
                {
                    WindowStartUtc = start.AddSeconds(i * 2),
                    WindowEndUtc = start.AddSeconds((i + 1) * 2),
                    DurationSeconds = 2.0,
                    Counts15 = Enumerable.Repeat(6000.0, 15).ToArray(),
                    RtState = "Hold",
                    RateTotalCps = 45000.0,
                    QualityScalar = 1.0,
                    IsConfusedCandidate = true
                };

                policy.AddWindow(summary);
                await Task.Delay(10);
            }

            var completed = await Task.WhenAny(publishTcs.Task, Task.Delay(2000));
            Assert.Equal(publishTcs.Task, completed);
            var publish = await publishTcs.Task;
            Assert.False(publish.LowStatistics);
            Assert.Equal("Single", publish.Label);
        }

        private sealed class StableLocalizer : RtILocalizer
        {
            public RtLocalizationPrediction Predict(RtLocalizationRequest request)
            {
                return new RtLocalizationPrediction
                {
                    IsOutOfDistribution = false,
                    MahalanobisDistance = 1.0,
                    ClassifierProbability = 0.95,
                    Label = "Single",
                    PredictedVector = new[] { 1.0, 2.0, 3.0 }
                };
            }
        }
    }
}
