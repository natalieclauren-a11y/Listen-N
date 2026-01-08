using System;
using System.Collections.Generic;
using System.Linq;
using Integrated.Contracts;
using Integrated.Runtime;
using RuntimeLocalizationRequest = Integrated.Runtime.LocalizationRequest;
using Xunit;

namespace Listen_N.Tests
{
    public sealed class LocalizationEpisodePolicyTests
    {
        [Fact]
        public void EpisodeStartsAfterDebounceWhenConfusedPersists()
        {
            var policy = new LocalizationEpisodePolicy(new LocalizationEpisodePolicyConfig
            {
                ConfuseDebounceWindows = 2,
                RecoverDebounceWindows = 2,
                ThrashWindowCount = 4,
                ThrashChangeThreshold = 3
            });

            bool started = false;
            policy.OnStatus += status =>
            {
                if (status.Code == LocalizationStatusCodes.EpisodeStarted)
                {
                    started = true;
                }
            };

            policy.AddWindow(BuildWindow("Hold", 1.0, 500, DateTimeOffset.UtcNow));
            Assert.False(policy.IsEpisodeActive);
            Assert.False(started);

            policy.AddWindow(BuildWindow("Hold", 1.0, 500, DateTimeOffset.UtcNow.AddSeconds(1)));
            Assert.True(policy.IsEpisodeActive);
            Assert.True(started);
        }

        [Fact]
        public void AccumulatorResetsOnRecoveryDebounce()
        {
            var policy = new LocalizationEpisodePolicy(new LocalizationEpisodePolicyConfig
            {
                ConfuseDebounceWindows = 2,
                RecoverDebounceWindows = 2
            });

            policy.AddWindow(BuildWindow("Hold", 1.0, 500, DateTimeOffset.UtcNow));
            policy.AddWindow(BuildWindow("Hold", 1.0, 500, DateTimeOffset.UtcNow.AddSeconds(1)));
            Assert.True(policy.IsEpisodeActive);
            Assert.True(policy.AccumulatedCountsTotal > 0);

            policy.AddWindow(BuildWindow("Normal", 1.0, 100, DateTimeOffset.UtcNow.AddSeconds(2)));
            Assert.True(policy.IsEpisodeActive);

            policy.AddWindow(BuildWindow("Normal", 1.0, 100, DateTimeOffset.UtcNow.AddSeconds(3)));
            Assert.False(policy.IsEpisodeActive);
            Assert.Equal(0, policy.AccumulatedCountsTotal);
        }

        [Fact]
        public void HardStopSchedulesFinalRequestAndEndsEpisodeAfterResult()
        {
            var policy = new LocalizationEpisodePolicy(new LocalizationEpisodePolicyConfig
            {
                ConfuseDebounceWindows = 1,
                RecoverDebounceWindows = 2,
                MaxPublishDurationSeconds = 60.0
            });

            var requests = new List<RuntimeLocalizationRequest>();
            policy.OnRequestMl += request => requests.Add(request);

            var start = DateTimeOffset.UtcNow;
            for (int i = 0; i < 6; i++)
            {
                policy.AddWindow(BuildWindow("Hold", 10.0, 1000, start.AddSeconds(i * 10)));
            }

            var finalRequest = Assert.Single(requests, request => !request.IsProbe);
            Assert.Equal(60.0, finalRequest.Row.DurationSeconds);
            Assert.NotEqual(Guid.Empty, finalRequest.EpisodeId);
            Assert.All(requests, request => Assert.Equal(finalRequest.EpisodeId, request.EpisodeId));
            var probeRequests = requests.Where(request => request.IsProbe).ToList();
            Assert.True(probeRequests.Count <= 1);
            if (probeRequests.Count == 1)
            {
                Assert.Equal(30.0, probeRequests[0].Row.DurationSeconds);
            }
            Assert.True(policy.IsEpisodeActive);

            policy.OnMlResult(finalRequest, new LocalizationPrediction
            {
                IsOutOfDistribution = false,
                MahalanobisDistance = 0.1,
                ClassifierProbability = 0.9,
                Label = "Single",
                PredictedVector = new[] { 1.0, 2.0, 3.0 }
            });

            Assert.False(policy.IsEpisodeActive);
        }

        [Fact]
        public void ProbeSchedulingBasedOnCountsIncrements()
        {
            var policy = new LocalizationEpisodePolicy(new LocalizationEpisodePolicyConfig
            {
                ConfuseDebounceWindows = 1,
                RecoverDebounceWindows = 2,
                CheckEveryCounts = 200
            });

            var requests = new List<RuntimeLocalizationRequest>();
            policy.OnRequestMl += request => requests.Add(request);

            var start = DateTimeOffset.UtcNow;
            policy.AddWindow(BuildWindow("Hold", 10.0, 50, start));
            policy.AddWindow(BuildWindow("Hold", 10.0, 100, start.AddSeconds(10)));
            policy.AddWindow(BuildWindow("Hold", 10.0, 200, start.AddSeconds(20)));

            Assert.Single(requests);
            Assert.True(requests[0].IsProbe);

            policy.AddWindow(BuildWindow("Hold", 10.0, 200, start.AddSeconds(30)));
            Assert.Single(requests);

            policy.OnMlResult(requests[0], new LocalizationPrediction
            {
                IsOutOfDistribution = false,
                MahalanobisDistance = 0.2,
                ClassifierProbability = 0.7,
                Label = "Single",
                PredictedVector = new[] { 1.0, 2.0, 3.0 }
            });

            policy.AddWindow(BuildWindow("Hold", 10.0, 200, start.AddSeconds(40)));
            Assert.Equal(2, requests.Count);
            Assert.True(requests[1].IsProbe);
        }

        [Fact]
        public void SingleStabilizesAndPublishesBeforeHardStop()
        {
            var policy = new LocalizationEpisodePolicy(new LocalizationEpisodePolicyConfig
            {
                ConfuseDebounceWindows = 1,
                RecoverDebounceWindows = 2,
                CheckEveryCounts = 1,
                StabilityK = 2,
                StabilityToleranceCm = 5.0,
                PublishProbabilityMin = 0.80
            });

            LocalizationPublishResult? published = null;
            policy.OnPublish += result => published = result;

            var request = CreateProbeRequest(policy, durationSeconds: 30.0, countsPerChannel: 2000);
            var prediction = new LocalizationPrediction
            {
                IsOutOfDistribution = false,
                MahalanobisDistance = 0.1,
                ClassifierProbability = 0.9,
                Label = "Single",
                PredictedVector = new[] { 0.0, 0.0, 0.0 }
            };

            policy.OnMlResult(request, prediction);
            policy.OnMlResult(request, prediction with { PredictedVector = new[] { 0.5, 0.5, 0.5 } });
            policy.OnMlResult(request, prediction with { PredictedVector = new[] { 0.8, 0.8, 0.8 } });

            Assert.NotNull(published);
            Assert.False(policy.IsEpisodeActive);
            Assert.False(published!.LowStatistics);
        }

        [Fact]
        public void DualOrderingSwapMaintainsStability()
        {
            var policy = new LocalizationEpisodePolicy(new LocalizationEpisodePolicyConfig
            {
                ConfuseDebounceWindows = 1,
                RecoverDebounceWindows = 2,
                CheckEveryCounts = 1,
                StabilityK = 1,
                StabilityToleranceCm = 5.0,
                PublishProbabilityMin = 0.80
            });

            LocalizationPublishResult? published = null;
            policy.OnPublish += result => published = result;

            var request = CreateProbeRequest(policy, durationSeconds: 30.0, countsPerChannel: 3000);
            var prediction = new LocalizationPrediction
            {
                IsOutOfDistribution = false,
                MahalanobisDistance = 0.1,
                ClassifierProbability = 0.9,
                Label = "Dual",
                PredictedVector = new[] { 0.0, 0.0, 0.0, 10.0, 0.0, 0.0 }
            };

            policy.OnMlResult(request, prediction);
            policy.OnMlResult(request, prediction with { PredictedVector = new[] { 10.0, 0.0, 0.0, 0.0, 0.0, 0.0 } });

            Assert.NotNull(published);
            Assert.False(policy.IsEpisodeActive);
        }

        [Fact]
        public void OscillatingPredictionsDoNotPublishUntilStable()
        {
            var policy = new LocalizationEpisodePolicy(new LocalizationEpisodePolicyConfig
            {
                ConfuseDebounceWindows = 1,
                RecoverDebounceWindows = 2,
                CheckEveryCounts = 1,
                StabilityK = 2,
                StabilityToleranceCm = 5.0,
                PublishProbabilityMin = 0.80
            });

            LocalizationPublishResult? published = null;
            policy.OnPublish += result => published = result;

            var request = CreateProbeRequest(policy, durationSeconds: 30.0, countsPerChannel: 2000);
            var prediction = new LocalizationPrediction
            {
                IsOutOfDistribution = false,
                MahalanobisDistance = 0.1,
                ClassifierProbability = 0.9,
                Label = "Single",
                PredictedVector = new[] { 0.0, 0.0, 0.0 }
            };

            policy.OnMlResult(request, prediction);
            policy.OnMlResult(request, prediction with { PredictedVector = new[] { 100.0, 0.0, 0.0 } });
            policy.OnMlResult(request, prediction with { PredictedVector = new[] { 0.0, 0.0, 0.0 } });
            policy.OnMlResult(request, prediction with { PredictedVector = new[] { 100.0, 0.0, 0.0 } });

            Assert.Null(published);
            Assert.True(policy.IsEpisodeActive);
        }

        [Fact]
        public void OodAtHardStopPublishesRefusalAndResets()
        {
            var policy = new LocalizationEpisodePolicy(new LocalizationEpisodePolicyConfig
            {
                ConfuseDebounceWindows = 1,
                RecoverDebounceWindows = 2,
                MaxPublishDurationSeconds = 60.0
            });

            LocalizationPublishResult? published = null;
            policy.OnPublish += result => published = result;

            var request = CreateFinalRequest(policy, durationSeconds: 60.0, countsPerChannel: 2000);
            policy.OnMlResult(request, new LocalizationPrediction
            {
                IsOutOfDistribution = true,
                MahalanobisDistance = 5.0,
                ClassifierProbability = 0.1,
                Label = "Single",
                PredictedVector = new[] { 1.0, 2.0, 3.0 }
            });

            Assert.NotNull(published);
            Assert.True(published!.IsOod);
            Assert.Equal("OOD at max duration", published.Reason);
            Assert.False(policy.IsEpisodeActive);
        }

        private static RtWindowSummary BuildWindow(string state, double durationSeconds, double countsPerChannel, DateTimeOffset start)
        {
            var counts = new double[15];
            for (int i = 0; i < counts.Length; i++)
            {
                counts[i] = countsPerChannel;
            }

            return new RtWindowSummary
            {
                WindowStartUtc = start,
                WindowEndUtc = start.AddSeconds(durationSeconds),
                DurationSeconds = durationSeconds,
                Counts15 = counts,
                RtState = state,
                RateTotalCps = countsPerChannel * counts.Length,
                QualityScalar = 10.0,
                IsConfusedCandidate = false
            };
        }

        private static RuntimeLocalizationRequest CreateProbeRequest(LocalizationEpisodePolicy policy, double durationSeconds, double countsPerChannel)
        {
            var requests = new List<RuntimeLocalizationRequest>();
            policy.OnRequestMl += request => requests.Add(request);

            var start = DateTimeOffset.UtcNow;
            policy.AddWindow(BuildWindow("Hold", durationSeconds, countsPerChannel, start));

            Assert.Single(requests);
            Assert.True(requests[0].IsProbe);
            return requests[0];
        }

        private static RuntimeLocalizationRequest CreateFinalRequest(LocalizationEpisodePolicy policy, double durationSeconds, double countsPerChannel)
        {
            var requests = new List<RuntimeLocalizationRequest>();
            policy.OnRequestMl += request => requests.Add(request);

            var start = DateTimeOffset.UtcNow;
            policy.AddWindow(BuildWindow("Hold", durationSeconds, countsPerChannel, start));

            Assert.Single(requests);
            Assert.False(requests[0].IsProbe);
            return requests[0];
        }
    }
}
