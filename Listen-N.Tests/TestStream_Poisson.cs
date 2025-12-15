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
     * Controller expectation: the adaptive engine should recognize the Poisson regime, remain
     *                         in Poisson state, contract to the smallest gate, and hold the
     *                         minimum analysis window without expanding.
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
        public void PoissonStream_EntersPoisson_StateAndContractsHold()
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
            engine.ZTrack = 3.0;
            engine.ZHold = 6.0;
            engine.MinGateCountForZ = 0;

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

            int poissonStart = estimates.FindIndex(e => e.State == "Poisson");
            Assert.True(poissonStart >= 0, "Engine never entered Poisson state.");

            const int tailCount = 8;
            var tailEstimates = estimates.TakeLast(Math.Min(tailCount, estimates.Count)).ToList();
            int tailPoissonCount = tailEstimates.Count(e => e.State == "Poisson");
            Assert.True(tailPoissonCount >= tailEstimates.Count - 1, "Poisson state not sustained in tail estimates.");

            var poissonEstimates = estimates.Where(e => e.State == "Poisson").ToList();
            Assert.NotEmpty(poissonEstimates);
            var tailPoisson = poissonEstimates.TakeLast(Math.Min(tailCount, poissonEstimates.Count)).ToList();

            foreach (var est in tailPoisson)
            {
                double absZY = Math.Abs(est.ZY);
                Assert.True(absZY < 4.0, $"Expected |Z_Y| < 4.0 in Poisson state, got {absZY:0.###}.");

                double yBound = 2.5 * est.SigmaY;
                Assert.InRange(Math.Abs(est.Y), 0.0, yBound);
            }

            const int smallestGateUs = 500;
            int stableGateCount = tailPoisson.Count(est => est.GateUs == smallestGateUs);
            Assert.True(stableGateCount >= tailPoisson.Count - 1, "Smallest gate not held in Poisson tail.");

            var gatesAfterPoisson = estimates.Skip(poissonStart).Select(e => e.GateUs).ToList();
            bool nonIncreasing = true;
            for (int i = 1; i < gatesAfterPoisson.Count; i++)
            {
                if (gatesAfterPoisson[i] > gatesAfterPoisson[i - 1])
                {
                    nonIncreasing = false;
                    break;
                }
            }
            Assert.True(nonIncreasing, "Gate sequence should contract or hold after Poisson entry.");

            int modeGate = tailPoisson
                .GroupBy(e => e.GateUs)
                .OrderByDescending(g => g.Count())
                .ThenBy(g => g.Key)
                .First()
                .Key;
            Assert.Equal(smallestGateUs, modeGate);

            foreach (var est in tailPoisson)
            {
                Assert.InRange(est.WindowSec, 0.5, 0.5 + 1e-6);
            }
        }
    }
}
