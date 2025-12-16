using System;
using System.Collections.Generic;
using System.Linq;
using Listen_N;
using Xunit;

namespace AdaptiveWindowTests
{
    public class TestStream_LowRateMode
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

        private static string DumpTail(IEnumerable<(long tUs, string state, int gateUs, double windowSec, bool hasSignificance, double y, double zY)> trace, int count = 20)
        {
            var tail = trace.TakeLast(Math.Min(count, trace.Count()))
                .Select(entry => $"t={entry.tUs / 1_000_000.0:F3}s state={entry.state} gate={entry.gateUs} win={entry.windowSec:F2}s sig={entry.hasSignificance} y={entry.y:F3} zy={entry.zY:F3}");
            return string.Join("; ", tail);
        }

        [Fact]
        public void VerySparseStream_EntersLowRate_ExpandsWindow_AndRemainsFinite()
        {
            var gateLadderUs = new[] { 500, 1000, 2000, 4000 };
            const double windowStartSec = 1.0;
            const double windowMinSec = 1.0;
            const double windowMaxSec = 30.0;

            using var engine = new AdaptiveWindowEngine(
                baseDeltaUs: 50,
                gateLadderUs: gateLadderUs,
                windowStartSec: windowStartSec,
                windowMinSec: windowMinSec,
                windowMaxSec: windowMaxSec,
                zMin: 1,
                epsY: 0.15,
                epsM1: 0.05,
                startWorker: false,
                enableFileLog: false);

            engine.ZPoisson = 4.0;
            engine.ZTrack = 4.0;
            engine.ZHold = 6.0;
            engine.MinGateCountForZ = 1;

            var estimates = new List<AdaptiveWindowEngine.Estimate>();
            var trace = new List<(long tUs, string state, int gateUs, double windowSec, bool hasSignificance, double y, double zY)>();
            engine.OnEstimate += est =>
            {
                estimates.Add(est);
                trace.Add((est.NowUs, est.State, est.GateUs, est.WindowSec, est.HasSignificance, est.Y, est.ZY));
            };

            var timestamps = GeneratePoissonTimestamps(seed: 8675309, rateHz: 0.5, durationSec: 120.0).ToList();
            Assert.NotEmpty(timestamps);

            long stepUs = 100_000; // 100 ms
            long durationUs = 120_000_000;
            long endUs = durationUs + 5_000_000;
            int idx = 0;

            for (long nowUs = 0; nowUs <= endUs; nowUs += stepUs)
            {
                while (idx < timestamps.Count && timestamps[idx] <= nowUs)
                {
                    engine.OnDetection(new Detection(timestamps[idx], 0));
                    idx++;
                }

                engine.ForceStep(nowUs);
            }

            Assert.True(estimates.Count > 10, "Expected multiple estimates across the sparse stream.");

            var lowRateSeen = estimates.Any(e => e.State == "LowRate");
            Assert.True(lowRateSeen, $"LowRate was not entered. Trace tail: {DumpTail(trace)}");

            double windowStart = estimates.First().WindowSec;
            double windowMaxObserved = estimates.Max(e => e.WindowSec);
            Assert.True(
                windowMaxObserved > windowStart + 0.5,
                $"Expected window to expand beyond start. Start={windowStart:F2}, Max={windowMaxObserved:F2}. Trace tail: {DumpTail(trace)}");

            Assert.DoesNotContain(estimates, e => e.State == "Degraded");

            foreach (var est in estimates)
            {
                Assert.True(double.IsFinite(est.WindowSec), $"WindowSec not finite at t={est.NowUs}. Trace tail: {DumpTail(trace)}");
                Assert.InRange(est.WindowSec, windowMinSec - 1e-6, windowMaxSec + 1e-6);
                Assert.Contains(est.GateUs, gateLadderUs);

                Assert.True(double.IsFinite(est.Y), "Y should remain finite.");
                Assert.True(double.IsFinite(est.ZY), "ZY should remain finite.");
                Assert.True(double.IsFinite(est.M1), "M1 should remain finite.");
                Assert.True(double.IsFinite(est.M2), "M2 should remain finite.");
                Assert.True(double.IsFinite(est.M3), "M3 should remain finite.");
                Assert.True(double.IsFinite(est.VarM1), "VarM1 should remain finite.");
                Assert.True(double.IsFinite(est.VarM2), "VarM2 should remain finite.");
                Assert.True(double.IsFinite(est.VarM3), "VarM3 should remain finite.");
                Assert.True(double.IsFinite(est.CovM1M2), "CovM1M2 should remain finite.");
                Assert.True(double.IsFinite(est.CovM1M3), "CovM1M3 should remain finite.");
                Assert.True(double.IsFinite(est.CovM2M3), "CovM2M3 should remain finite.");
            }
        }
    }
}
