using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Listen_N;
using Xunit;
using Xunit.Sdk;

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
            long lastPrimaryUs = -1;
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
                if (primaryUs <= lastPrimaryUs)
                {
                    primaryUs = lastPrimaryUs + 1;
                }

                timestamps.Add(primaryUs);
                lastPrimaryUs = primaryUs;

                if (rng.NextDouble() <= pairFraction)
                {
                    double dtPairSec;
                    do
                    {
                        double v;
                        do
                        {
                            v = rng.NextDouble();
                        }
                        while (v <= 0.0 || v >= 1.0);

                        dtPairSec = -Math.Log(1 - v) * tauSec;
                    }
                    while (dtPairSec < 0.1 * tauSec || dtPairSec > 4.0 * tauSec);

                    double pairedEventSec = tSec + dtPairSec;
                    if (pairedEventSec <= durationSec)
                    {
                        long dtPairUs = Math.Max(1L, (long)Math.Round(dtPairSec * 1e6));
                        long pairedUs = primaryUs + dtPairUs;

                        timestamps.Add(pairedUs);
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

            var steady = estimates.Where(e => e.State != "Warmup" && e.State != "LowRate").ToList();
            Assert.NotEmpty(steady);

            var tail = steady.TakeLast(Math.Min(20, steady.Count)).ToList();

            var bindingFlags = BindingFlags.Instance | BindingFlags.NonPublic;
            var engineType = typeof(AdaptiveWindowEngine);
            double[]? yByGate = null;
            double[]? sYByGate = null;

            var doubleArrayFields = engineType
                .GetFields(bindingFlags)
                .Where(f => f.FieldType == typeof(double[]))
                .Select(f => new { Field = f, Value = f.GetValue(engine) as double[] })
                .Where(p => p.Value != null)
                .ToList();

            var ladderFields = doubleArrayFields
                .Where(p => p.Value!.Length == gateLadderUs.Length)
                .Select(p =>
                {
                    double[] values = p.Value!;
                    var finiteValues = values.Where(double.IsFinite).ToArray();
                    int finiteCount = finiteValues.Length;
                    double absMean = finiteCount > 0 ? finiteValues.Select(Math.Abs).Average() : double.NaN;
                    bool anyNonZero = values.Any(v => v != 0.0);
                    bool nonNegative = values.Where(double.IsFinite).All(v => v >= 0.0);
                    return new
                    {
                        p.Field,
                        Values = values,
                        FiniteCount = finiteCount,
                        AbsMean = absMean,
                        AnyNonZero = anyNonZero,
                        NonNegative = nonNegative
                    };
                })
                .ToList();

            var yCandidate = ladderFields
                .Where(c => c.FiniteCount > 0 && c.AnyNonZero)
                .OrderByDescending(c => c.AbsMean)
                .FirstOrDefault();

            if (yCandidate != null)
            {
                yByGate = yCandidate.Values;

                var syCandidate = ladderFields
                    .Where(c => c.Field != yCandidate.Field)
                    .Where(c => c.FiniteCount > 0)
                    .Where(c => c.NonNegative)
                    .Where(c => !(double.IsFinite(c.AbsMean) && double.IsFinite(yCandidate.AbsMean) && c.AbsMean > 1.5 * yCandidate.AbsMean))
                    .OrderBy(c => c.AbsMean)
                    .FirstOrDefault();

                if (syCandidate != null)
                {
                    sYByGate = syCandidate.Values;
                }
            }

            var ladderFieldDump = doubleArrayFields.Any()
                ? string.Join(", ", doubleArrayFields.Select(p => $"{p.Field.Name}[{p.Value!.Length}]"))
                : "<none>";

            double[]? zByGate = null;
            if (yByGate != null && sYByGate != null)
            {
                zByGate = yByGate.Zip(sYByGate, (y, sy) => sy > 0 ? y / sy : double.NaN).ToArray();

                Assert.True(zByGate.Any(z => double.IsFinite(z) && z > 1.0),
                    "Correlated plant produced no significant positive Y at any gate. This indicates a plant/test parameter issue or an engine regression in ladder computation.");
            }

            int poissonTailCount = tail.Count(e => e.State == "Poisson");
            Assert.True(poissonTailCount <= 2, $"Expected correlated tail to avoid Poisson; got {poissonTailCount}/{tail.Count} Poisson.");
            Assert.NotEqual("Poisson", tail[^1].State);
            int positiveY = tail.Count(e => e.Y > 0 && e.ZY > 1.0);
            if (positiveY < 0.7 * tail.Count)
            {
                string tailDump = string.Join("; ", tail.Select(e => $"state={e.State}, gate={e.GateUs}, Y={e.Y:F3}, ZY={e.ZY:F3}"));
                string ladderDump = zByGate != null && yByGate != null
                    ? string.Join("; ", gateLadderUs.Select((gate, idx) =>
                    {
                        double yVal = idx < yByGate.Length ? yByGate[idx] : double.NaN;
                        double zVal = idx < zByGate.Length ? zByGate[idx] : double.NaN;
                        return $"gate={gate}us: Y={yVal:F3}, ZY={zVal:F3}";
                    }))
                    : $"No ladder resolved; discovered double[] fields: {ladderFieldDump}";

                throw new XunitException(
                    "Expected correlated stream to yield predominantly positive Y values with meaningful Z." + Environment.NewLine +
                    $"positiveY={positiveY}, tailCount={tail.Count}" + Environment.NewLine +
                    $"Tail: {tailDump}" + Environment.NewLine +
                    $"Ladder: {ladderDump}");
            }

            int modalGateUs = tail
                .GroupBy(e => e.GateUs)
                .OrderByDescending(g => g.Count())
                .ThenBy(g => g.Key)
                .First()
                .Key;

            int maxKneeGateUs = (int)Math.Round(2.0 * tauSec * 1e6); // 2*tau in µs, here ~4000
            Assert.True(modalGateUs <= maxKneeGateUs,
                $"Expected Tg near or below correlation scale: Tg={modalGateUs}us, 2*tau={maxKneeGateUs}us.");

            Assert.NotEqual(gateLadderUs[^1], modalGateUs); // should not drift to largest gate
            int modalCount = tail.Count(e => e.GateUs == modalGateUs);
            Assert.True(modalCount >= (int)(0.6 * tail.Count), "Gate should stabilize near the correlation knee.");

            var tauField = typeof(AdaptiveWindowEngine).GetField("_tauHat", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(tauField);
            double tauHatSec = (double)(tauField!.GetValue(engine) ?? double.NaN);
            Assert.True(double.IsFinite(tauHatSec) && tauHatSec > 0, "Tau estimate should be finite and positive.");

            double relativeError = Math.Abs(tauHatSec - tauSec) / tauSec;
            Assert.True(relativeError < 0.6, $"Tau estimate should recover the planted correlation time. tau_hat={tauHatSec:E3}s");
        }
    }
}
