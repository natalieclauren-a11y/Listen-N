using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Listen_N;
using Xunit;

namespace AdaptiveWindowTests
{
    public class TestStream_CorrelatedSource
    {
        private static IEnumerable<long> GenerateCorrelatedPairs(
            int seed,
            double baseRateHz,
            double pairFraction,
            double tauSec,
            double durationSec)
        {
            var rng = new Random(seed);
            double tSec = 0.0;
            long lastUs = -1;
            var timestamps = new List<long>();

            while (tSec < durationSec)
            {
                double u;
                do
                {
                    u = rng.NextDouble();
                }
                while (u <= 0.0 || u >= 1.0);

                double dtSec = -Math.Log(1 - u) / baseRateHz;
                tSec += dtSec;
                if (tSec > durationSec)
                {
                    break;
                }

                long primaryUs = (long)Math.Round(tSec * 1e6);
                if (primaryUs <= lastUs)
                {
                    primaryUs = lastUs + 1;
                }

                timestamps.Add(primaryUs);
                lastUs = primaryUs;

                if (rng.NextDouble() <= pairFraction)
                {
                    double v;
                    do
                    {
                        v = rng.NextDouble();
                    }
                    while (v <= 0.0 || v >= 1.0);

                    double dtPairSec = -Math.Log(1 - v) * tauSec;
                    double pairedEventSec = tSec + dtPairSec;
                    if (pairedEventSec <= durationSec)
                    {
                        long pairedUs = (long)Math.Round(pairedEventSec * 1e6);
                        if (pairedUs <= lastUs)
                        {
                            pairedUs = lastUs + 1;
                        }

                        timestamps.Add(pairedUs);
                        lastUs = pairedUs;
                    }
                }
            }

            timestamps.Sort();
            long prev = -1;
            for (int i = 0; i < timestamps.Count; i++)
            {
                if (timestamps[i] <= prev)
                {
                    timestamps[i] = prev + 1;
                }

                prev = timestamps[i];
            }

            return timestamps;
        }

        [Fact]
        public void CorrelatedPairs_ProduceStableCorrelationEstimate()
        {
            var gateLadderUs = new[] { 250, 500, 1000, 2000, 4000, 8000, 16000 };

            using var engine = new AdaptiveWindowEngine(
                baseDeltaUs: 50,
                gateLadderUs: gateLadderUs,
                windowStartSec: 1.0,
                windowMinSec: 1.0,
                windowMaxSec: 60.0,
                zMin: 2,
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

            double tauSec = 0.002; // 2 ms correlation time
            var timestamps = GenerateCorrelatedPairs(
                seed: 24680,
                baseRateHz: 2200.0,
                pairFraction: 0.5,
                tauSec: tauSec,
                durationSec: 11.0).ToList();

            Assert.NotEmpty(timestamps);

            foreach (long ts in timestamps)
            {
                engine.OnDetection(new Detection(ts, 0));
            }

            long nowUs = timestamps[^1] + 1;
            for (int i = 0; i < 12; i++)
            {
                nowUs += 500_000; // +0.5 s per step
                engine.ForceStep(nowUs);
            }

            Assert.NotEmpty(estimates);
            Assert.DoesNotContain(estimates, e => e.State == "Poisson");

            var steady = estimates.Where(e => e.State != "Warmup" && e.State != "LowRate").ToList();
            Assert.NotEmpty(steady);

            var tail = steady.TakeLast(Math.Min(20, steady.Count)).ToList();
            int positiveY = tail.Count(e => e.Y > 0 && e.HasSignificance);
            Assert.True(positiveY >= tail.Count / 2, "Expected correlated stream to yield positive significant Y values.");

            int modalGateUs = tail
                .GroupBy(e => e.GateUs)
                .OrderByDescending(g => g.Count())
                .ThenBy(g => g.Key)
                .First()
                .Key;

            Assert.Contains(modalGateUs, new[] { 2000, 4000 });
            int modalCount = tail.Count(e => e.GateUs == modalGateUs);
            Assert.True(modalCount >= (int)(0.6 * tail.Count), "Gate should stabilize near the correlation knee.");

            var tauField = typeof(AdaptiveWindowEngine).GetField("_tauHat", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(tauField);
            double tauHatSec = (double)(tauField!.GetValue(engine) ?? double.NaN);
            Assert.True(double.IsFinite(tauHatSec) && tauHatSec > 0, "Tau estimate should be finite and positive.");

            double relativeError = Math.Abs(tauHatSec - tauSec) / tauSec;
            Assert.True(relativeError < 0.6, $"Tau estimate should recover the planted correlation time. tau_hat={tauHatSec:0.###}s");
        }
    }
}
