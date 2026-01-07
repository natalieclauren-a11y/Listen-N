using System;
using System.Collections.Generic;
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

            Assert.Single(requests);
            Assert.False(requests[0].IsProbe);
            Assert.True(policy.IsEpisodeActive);

            policy.OnMlResult(requests[0], new LocalizationPrediction
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
            policy.AddWindow(BuildWindow("Hold", 1.0, 50, start));
            policy.AddWindow(BuildWindow("Hold", 1.0, 100, start.AddSeconds(1)));
            policy.AddWindow(BuildWindow("Hold", 1.0, 200, start.AddSeconds(2)));

            Assert.Single(requests);
            Assert.True(requests[0].IsProbe);

            policy.AddWindow(BuildWindow("Hold", 1.0, 200, start.AddSeconds(3)));
            Assert.Single(requests);

            policy.OnMlResult(requests[0], new LocalizationPrediction
            {
                IsOutOfDistribution = false,
                MahalanobisDistance = 0.2,
                ClassifierProbability = 0.7,
                Label = "Single",
                PredictedVector = new[] { 1.0, 2.0, 3.0 }
            });

            policy.AddWindow(BuildWindow("Hold", 1.0, 200, start.AddSeconds(4)));
            Assert.Equal(2, requests.Count);
            Assert.True(requests[1].IsProbe);
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
    }
}
