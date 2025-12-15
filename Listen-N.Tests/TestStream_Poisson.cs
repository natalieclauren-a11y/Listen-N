using System;
using System.Collections.Generic;
using System.Linq;
using Listen_N;
using Xunit;

namespace AdaptiveWindowTests
{
    /*
     * Plant: a stationary Poisson process with fixed rate generates uncorrelated timestamps.
     * Observation: the Feynman-Y estimator (Y, ZY) should fluctuate around zero with variance
     *              governed by SigmaY, reflecting Poisson counting noise only.
     * Controller expectation: in this controller, a Poisson field routes to LowRate because the
     *                         significance gate requires Y > 0 to entertain other states, so the
     *                         gate should shrink while the window expands as LowRate stabilizes.
     */
    public class TestStream_Poisson
    {
        private static IEnumerable<long> GeneratePoissonTimestamps(int seed, double rateHz, double durationSec)
        {
            var rng = new Random(seed);
            double tSec = 0.0;
            long lastUs = -1;

            while (tSec < durationSec)
            {
                double u;
                do
                {
                    u = rng.NextDouble();
                }
                while (u <= 0.0 || u >= 1.0);

                double dtSec = -Math.Log(1 - u) / rateHz;
                tSec += dtSec;

                long tsUs = (long)Math.Round(tSec * 1e6);
                if (tsUs <= lastUs)
                {
                    tsUs = lastUs + 1;
                }

                if (tSec <= durationSec)
                {
                    yield return tsUs;
                }

                lastUs = tsUs;
            }
        }

        [Fact]
        public void PoissonStream_TriggersLowRate_ExpandsWindow_UsesSmallestGate()
        {
            var gateLadder = new[] { 500, 1000, 2000 };

            using var engine = new AdaptiveWindowEngine(
                baseDeltaUs: 50,
                gateLadderUs: gateLadder,
                windowStartSec: 0.5,
                windowMinSec: 0.5,
                windowMaxSec: 60.0,
                zMin: 1,
                epsY: 0.10,
                epsM1: 0.02,
                startWorker: false,
                enableFileLog: false);

            engine.ZPoisson = 4.0;
            engine.ZTrack = 4.0;
            engine.ZHold = 6.0;
            engine.MinGateCountForZ = 2;

            var estimates = new List<AdaptiveWindowEngine.Estimate>();
            engine.OnEstimate += estimates.Add;

            var timestamps = GeneratePoissonTimestamps(seed: 12345, rateHz: 2000.0, durationSec: 8.0).ToList();
            Assert.True(timestamps.Count > 0);

            foreach (long ts in timestamps)
            {
                engine.OnDetection(new Detection(ts, 0));
            }

            long nowUs = timestamps[^1] + 1;
            engine.ForceStep(nowUs);

            Assert.True(estimates.Count >= 5, "Expected multiple estimates from Poisson stream.");

            const int smallestGateUs = 500;
            Assert.DoesNotContain(estimates, e => e.State == "Hold");

            var lowRateEstimates = estimates.Where(e => e.State == "LowRate").ToList();
            Assert.NotEmpty(lowRateEstimates);

            var lowRateTail = lowRateEstimates.TakeLast(Math.Min(8, lowRateEstimates.Count)).ToList();
            int smallestGateCount = lowRateTail.Count(est => est.GateUs == smallestGateUs);
            Assert.True(
                smallestGateCount >= lowRateTail.Count - 1,
                "Expected LowRate tail to hold the smallest gate for nearly all estimates.");

            bool windowNonDecreasing = true;
            for (int i = 1; i < lowRateEstimates.Count; i++)
            {
                if (lowRateEstimates[i].WindowSec < lowRateEstimates[i - 1].WindowSec)
                {
                    windowNonDecreasing = false;
                    break;
                }
            }
            Assert.True(windowNonDecreasing, "Window should expand or hold within LowRate estimates.");
        }

        [Fact]
        public void PoissonStream_DoesNotProduceLargeCorrelationSignals()
        {
            var gateLadder = new[] { 500, 1000, 2000 };

            using var engine = new AdaptiveWindowEngine(
                baseDeltaUs: 50,
                gateLadderUs: gateLadder,
                windowStartSec: 0.5,
                windowMinSec: 0.5,
                windowMaxSec: 60.0,
                zMin: 1,
                epsY: 0.10,
                epsM1: 0.02,
                startWorker: false,
                enableFileLog: false);

            engine.ZPoisson = 4.0;
            engine.ZTrack = 4.0;
            engine.ZHold = 6.0;
            engine.MinGateCountForZ = 2;

            var estimates = new List<AdaptiveWindowEngine.Estimate>();
            engine.OnEstimate += estimates.Add;

            var timestamps = GeneratePoissonTimestamps(seed: 12345, rateHz: 2000.0, durationSec: 8.0).ToList();
            Assert.True(timestamps.Count > 0);

            foreach (long ts in timestamps)
            {
                engine.OnDetection(new Detection(ts, 0));
            }

            long nowUs = timestamps[^1] + 1;
            engine.ForceStep(nowUs);

            const int tailCount = 8;
            var tailEstimates = estimates.TakeLast(Math.Min(tailCount, estimates.Count)).ToList();
            foreach (var est in tailEstimates)
            {
                Assert.True(Math.Abs(est.ZY) < engine.ZHold, $"Expected |ZY| < {engine.ZHold}, got {est.ZY:0.###}.");
            }

            int relaxedBoundCount = tailEstimates.Count(est => Math.Abs(est.ZY) < 4.0);
            Assert.True(
                relaxedBoundCount >= tailEstimates.Count - 1,
                "Expected most tail estimates to have |ZY| < 4.0 in Poisson field.");
        }
    }
}
