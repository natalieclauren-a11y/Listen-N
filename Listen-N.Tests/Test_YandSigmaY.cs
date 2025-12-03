using System;
using System.Collections.Generic;
using System.Linq;
using Listen_N;
using Xunit;

namespace AdaptiveWindowTests
{
    public class Test_YandSigmaY
    {
        [Fact]
        public void Test_YNearZeroForLowVariance()
        {
            var pattern = new[] { 10, 10, 10, 10 };

            var estimate = GenerateEstimate(pattern);

            Assert.True(double.IsFinite(estimate.Y), "Y should be finite for low-variance pattern");
            Assert.True(double.IsFinite(estimate.SigmaY), "SigmaY should be finite for low-variance pattern");
            Assert.True(estimate.Y < 0, "Y should be negative for nearly constant gates");
            Assert.InRange(estimate.Y, -2.0, 0.0);
        }

        [Fact]
        public void Test_YPositiveForClusteredData()
        {
            var pattern = new[] { 1, 5, 1, 5, 1, 5 };

            var estimate = GenerateEstimate(pattern);

            Assert.True(double.IsFinite(estimate.Y), "Y should be finite for clustered gates");
            Assert.True(estimate.Y > 0, "Clustered gates should yield positive Y");
        }

        [Fact]
        public void Test_YRespondsToVarianceIncrease()
        {
            var lowVariance = new[] { 4, 4, 4, 4 };
            var highVariance = new[] { 1, 10, 1, 10 };

            var lowVarEstimate = GenerateEstimate(lowVariance);
            var highVarEstimate = GenerateEstimate(highVariance);

            Assert.True(double.IsFinite(lowVarEstimate.Y), "Y for low-variance pattern should be finite");
            Assert.True(double.IsFinite(highVarEstimate.Y), "Y for high-variance pattern should be finite");
            Assert.True(highVarEstimate.Y > lowVarEstimate.Y, "Higher variance should produce larger Y");
        }

        private static AdaptiveWindowEngine.Estimate GenerateEstimate(IReadOnlyList<int> gateCounts)
        {
            const int gateUs = 200;
            double rawWindowSec = gateCounts.Count * gateUs / 1e6;
            double windowSec = Math.Max(1e-3, rawWindowSec);
            int deltaUs = Math.Max(1, gateUs / 10);

            using var engine = new AdaptiveWindowEngine(
                baseDeltaUs: deltaUs,
                windowStartSec: windowSec,
                windowMinSec: windowSec,
                windowMaxSec: windowSec,
                gateLadderUs: new[] { gateUs },
                startWorker: false,
                minGateCountForZ: 0);

            var timestamps = BuildTimestamps(gateCounts, gateUs);

            engine.ResetTimestampState(timestamps[0]);

            AdaptiveWindowEngine.Estimate? lastEstimate = null;
            void OnEstimate(AdaptiveWindowEngine.Estimate est) => lastEstimate = est;
            engine.OnEstimate += OnEstimate;

            foreach (long ts in timestamps)
            {
                engine.OnDetection(new Detection(ts));
                engine.ForceStep(ts);
            }

            long finalTs = timestamps[^1] + gateUs;
            engine.ForceStep(finalTs);

            engine.OnEstimate -= OnEstimate;

            return lastEstimate ?? throw new InvalidOperationException("No estimate was produced.");
        }

        private static List<long> BuildTimestamps(IReadOnlyList<int> gateCounts, int gateUs)
        {
            var timestamps = new List<long>();
            long startUs = gateUs; // avoid zero so accumulator initializes _t0Us

            for (int i = 0; i < gateCounts.Count; i++)
            {
                long gateStart = startUs + (long)i * gateUs;
                int count = gateCounts[i];
                for (int j = 0; j < count; j++)
                {
                    long ts = gateStart + j + 1; // keep events within the gate bounds
                    timestamps.Add(ts);
                }
            }

            if (timestamps.Count == 0)
                throw new ArgumentException("At least one gate count is required", nameof(gateCounts));

            return timestamps;
        }
    }
}
