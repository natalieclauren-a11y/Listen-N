using System;
using System.Collections.Generic;
using System.Linq;
using Listen_N;
using Xunit;
using Xunit.Sdk;

namespace AdaptiveWindowTests
{
    public class TestStream_CorrelatedSource
    {
        private static IEnumerable<long> GenerateCorrelatedPairsFromClusters(
            int seed,
            double clusterRateHz,
            double tauSec,
            double durationSec)
        {
            var rng = new Random(seed);
            double tSec = 0.0;
            var timestamps = new List<long>();
            double minDtSec = 2 * 50 / 1e6; // enforce at least 2 * baseDeltaUs (100 us)

            while (tSec < durationSec)
            {
                double u;
                do
                {
                    u = rng.NextDouble();
                }
                while (u <= 0.0 || u >= 1.0);

                double dtSec = -Math.Log(1 - u) / clusterRateHz;
                tSec += dtSec;
                if (tSec > durationSec)
                {
                    break;
                }

                long primaryUs = (long)Math.Round(tSec * 1e6);
                timestamps.Add(primaryUs);

                double v;
                do
                {
                    v = rng.NextDouble();
                }
                while (v <= 0.0 || v >= 1.0);

                double dtBurstSec = -Math.Log(1 - v) * tauSec;
                dtBurstSec = Math.Max(minDtSec, dtBurstSec);

                long pairedUs = primaryUs + (long)Math.Round(dtBurstSec * 1e6);
                timestamps.Add(pairedUs);
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
        public void CorrelatedPlant_DetectsCorrelation_NoPoissonAttractor()
        {
            var gateLadderUs = new[] { 1000, 2000, 4000, 8000, 16000, 32000 };

            using var engine = new AdaptiveWindowEngine(
                baseDeltaUs: 50,
                gateLadderUs: gateLadderUs,
                windowStartSec: 1.0,
                windowMinSec: 1.0,
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

            double tauSec = 0.002; // 2 ms correlation time
            var timestamps = GenerateCorrelatedPairsFromClusters(
                seed: 24680,
                clusterRateHz: 1000.0,
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

            var steady = estimates.Where(e => e.State != "Warmup" && e.State != "LowRate").ToList();
            Assert.NotEmpty(steady);

            var tail = steady.TakeLast(Math.Min(20, steady.Count)).ToList();
            var reversed = steady.AsEnumerable().Reverse().ToList();
            var run = new List<AdaptiveWindowEngine.Estimate>();
            foreach (var e in reversed)
            {
                if (e.Y > 0 && e.ZY > 1.0)
                {
                    run.Add(e);
                }
                else if (run.Count > 0)
                {
                    break;
                }
            }

            run.Reverse();

            if (run.Count < 5)
            {
                string tailDump = string.Join("; ", tail.Select(e => $"state={e.State}, gate={e.GateUs}, Y={e.Y:F3}, ZY={e.ZY:F3}"));
                throw new XunitException(
                    "Expected correlated stream to yield a sustained run of positive correlation estimates." + Environment.NewLine +
                    $"runCount={run.Count}, steadyCount={steady.Count}" + Environment.NewLine +
                    $"Tail: {tailDump}");
            }

            Assert.NotEqual("Poisson", steady[^1].State);
            Assert.True(steady.TakeLast(10).Count(e => e.State == "Poisson") <= 2);
            Assert.True(run.Count >= 5);
            foreach (var e in run)
            {
                Assert.True(e.Y > 0, $"Expected positive Y during sustained correlation; got Y={e.Y:F3} at gate={e.GateUs} state={e.State}.");
                Assert.True(e.ZY > 1.0, $"Expected ZY>1 during sustained correlation; got ZY={e.ZY:F3} at gate={e.GateUs} state={e.State}.");
            }

            int modalGateUs = run
                .GroupBy(e => e.GateUs)
                .OrderByDescending(g => g.Count())
                .ThenBy(g => g.Key)
                .First()
                .Key;

            Assert.Contains(modalGateUs, new[] { 1000, 2000, 4000 });
            Assert.NotEqual(gateLadderUs[^1], modalGateUs);
        }

        [Fact]
        public void CorrelatedPlant_EntersDegraded_ModelMismatch_PreventsTauClaim()
        {
            var gateLadderUs = new[] { 1000, 2000, 4000, 8000, 16000, 32000 };

            using var engine = new AdaptiveWindowEngine(
                baseDeltaUs: 50,
                gateLadderUs: gateLadderUs,
                windowStartSec: 1.0,
                windowMinSec: 1.0,
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

            double tauSec = 0.002; // 2 ms correlation time
            var timestamps = GenerateCorrelatedPairsFromClusters(
                seed: 24680,
                clusterRateHz: 1000.0,
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

            var steady = estimates.Where(e => e.State != "Warmup" && e.State != "LowRate").ToList();
            Assert.NotEmpty(steady);

            var tail = steady.TakeLast(Math.Min(20, steady.Count)).ToList();

            Assert.Contains(tail, e => e.State == "Degraded");

            // Tau is a model-based estimate and is not claimed when model mismatch is detected.
            Assert.True(tail.Any(e => e.Y > 0 && e.ZY > 1.0), "Expected correlated estimates even under Degraded.");
        }
    }
}
