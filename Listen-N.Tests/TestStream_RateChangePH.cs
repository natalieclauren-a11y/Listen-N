using System;
using System.Collections.Generic;
using System.Linq;
using Listen_N;
using Xunit;
using Xunit.Sdk;

namespace AdaptiveWindowTests
{
    public class TestStream_RateChangePH
    {
        private static IEnumerable<long> GeneratePoissonSegment(int seed, long startUs, double durationSec, double rateCps)
        {
            var rng = new Random(seed);
            double tSec = startUs / 1e6;
            long lastUs = startUs - 1;
            long endUs = startUs + (long)Math.Round(durationSec * 1e6);

            while (true)
            {
                double u;
                do
                {
                    u = rng.NextDouble();
                }
                while (u <= 0.0 || u >= 1.0);

                double dtSec = -Math.Log(1 - u) / rateCps;
                tSec += dtSec;

                long tsUs = (long)Math.Round(tSec * 1e6);
                if (tsUs > endUs)
                {
                    yield break;
                }

                if (tsUs <= lastUs)
                {
                    tsUs = lastUs + 1;
                }

                yield return tsUs;
                lastUs = tsUs;
            }
        }

        private static IEnumerable<long> GenerateClusteredSegment(
            int seed,
            long startUs,
            double durationSec,
            double parentRateCps,
            int meanClusterSize,
            double childMeanDelayUs)
        {
            var rng = new Random(seed);
            double tSec = startUs / 1e6;
            long lastUs = startUs - 1;
            long endUs = startUs + (long)Math.Round(durationSec * 1e6);

            int SamplePoisson(double lambda)
            {
                if (lambda <= 0)
                {
                    return 0;
                }

                double l = Math.Exp(-lambda);
                int k = 0;
                double p = 1.0;

                do
                {
                    k++;
                    p *= rng.NextDouble();
                }
                while (p > l);

                return k - 1;
            }

            double SampleExponential(double mean)
            {
                double u;
                do
                {
                    u = rng.NextDouble();
                }
                while (u <= 0.0 || u >= 1.0);

                return -Math.Log(1 - u) * mean;
            }

            while (true)
            {
                double u;
                do
                {
                    u = rng.NextDouble();
                }
                while (u <= 0.0 || u >= 1.0);

                double dtSec = -Math.Log(1 - u) / parentRateCps;
                tSec += dtSec;

                long parentUs = (long)Math.Round(tSec * 1e6);
                if (parentUs > endUs)
                {
                    yield break;
                }

                if (parentUs <= lastUs)
                {
                    parentUs = lastUs + 1;
                }

                int children = 1 + SamplePoisson(Math.Max(0, meanClusterSize - 1));
                var childTimes = new List<long>(children);

                for (int i = 0; i < children; i++)
                {
                    double delayUs = SampleExponential(childMeanDelayUs);
                    long childUs = parentUs + (long)Math.Round(delayUs);
                    childTimes.Add(childUs);
                }

                childTimes.Sort();

                foreach (var childUs in childTimes)
                {
                    if (childUs > endUs)
                    {
                        yield break;
                    }

                    long tsUs = childUs;
                    if (tsUs <= lastUs)
                    {
                        tsUs = lastUs + 1;
                    }

                    yield return tsUs;
                    lastUs = tsUs;
                }
            }
        }

        private static string DumpTail(IEnumerable<(long tUs, string state, int gateUs, bool hasSignificance, double y, double zY, double windowSec)> trace, int count = 20)
        {
            var tail = trace.TakeLast(Math.Min(count, trace.Count()))
                .Select(entry => $"t={entry.tUs / 1_000_000.0:F3}s state={entry.state} gate={entry.gateUs} win={entry.windowSec:F2}s sig={entry.hasSignificance} y={entry.y:F3} zy={entry.zY:F3}");
            return string.Join("; ", tail);
        }

        [Fact]
        public void RateStep_PHTriggersHold_PersistsAcrossTicks()
        {
            var gateLadderUs = new[] { 500, 1000, 2000, 4000 };

            using var engine = new AdaptiveWindowEngine(
                baseDeltaUs: 50,
                gateLadderUs: gateLadderUs,
                windowStartSec: 1.0,
                windowMinSec: 1.0,
                windowMaxSec: 6.0,
                zMin: 1,
                epsY: 0.20,
                epsM1: 0.10,
                startWorker: false,
                enableFileLog: false);

            engine.Beta = 0.10;
            engine.ZPoisson = 4.0;
            engine.ZTrack = 4.0;
            engine.ZHold = 6.0;
            engine.MinGateCountForZ = 2;

            var estimates = new List<AdaptiveWindowEngine.Estimate>();
            var trace = new List<(long tUs, string state, int gateUs, bool hasSignificance, double y, double zY, double windowSec)>();
            engine.OnEstimate += est =>
            {
                estimates.Add(est);
                trace.Add((est.NowUs, est.State, est.GateUs, est.HasSignificance, est.Y, est.ZY, est.WindowSec));
            };

            double phaseAParentRateCps = 200.0;
            double phaseAWindowSec = 20.0;
            double phaseBDurationSec = 12.0;
            double phaseBParentRateCps = 2000.0;
            int meanClusterSize = 2;
            double childMeanDelayUs = 800.0;

            long stepIntervalUs = 20_000; // 20 ms
            long phaseAEndUs = 0;

            int calmNeeded = 2;
            int calmCount = 0;

            var phaseAGen = GenerateClusteredSegment(seed: 13579, startUs: 0, durationSec: phaseAWindowSec, parentRateCps: phaseAParentRateCps, meanClusterSize: meanClusterSize, childMeanDelayUs: childMeanDelayUs)
                .GetEnumerator();
            bool phaseAHasEvent = phaseAGen.MoveNext();

            long phaseAWindowEndUs = (long)(phaseAWindowSec * 1e6);
            long lastNowUs = 0;

            for (long nowUs = stepIntervalUs; nowUs <= phaseAWindowEndUs; nowUs += stepIntervalUs)
            {
                while (phaseAHasEvent && phaseAGen.Current <= nowUs)
                {
                    engine.OnDetection(new Detection(phaseAGen.Current, 0));
                    phaseAHasEvent = phaseAGen.MoveNext();
                }

                int prevCount = estimates.Count;
                engine.ForceStep(nowUs);
                lastNowUs = nowUs;

                if (estimates.Count > prevCount)
                {
                    var latestEstimate = estimates[^1];

                    bool isSettled = latestEstimate.State == "Track";
                    if (isSettled)
                    {
                        calmCount++;
                        if (calmCount >= calmNeeded)
                        {
                            phaseAEndUs = latestEstimate.NowUs;
                            break;
                        }
                    }
                    else
                    {
                        calmCount = 0;
                    }
                }
            }

            if (phaseAEndUs == 0)
            {
                throw new XunitException(
                    "Did not reach calm state before applying step." + Environment.NewLine +
                    DumpTail(trace));
            }

            long stepStartUs = phaseAEndUs + stepIntervalUs;

            var phaseBGen = GenerateClusteredSegment(seed: 24680, startUs: phaseAEndUs, durationSec: phaseBDurationSec, parentRateCps: phaseBParentRateCps, meanClusterSize: meanClusterSize, childMeanDelayUs: childMeanDelayUs)
                .GetEnumerator();
            bool phaseBHasEvent = phaseBGen.MoveNext();

            long phaseBEndUs = phaseAEndUs + (long)(phaseBDurationSec * 1e6);

            for (long nowUs = phaseAEndUs + stepIntervalUs; nowUs <= phaseBEndUs; nowUs += stepIntervalUs)
            {
                while (phaseBHasEvent && phaseBGen.Current <= nowUs)
                {
                    engine.OnDetection(new Detection(phaseBGen.Current, 0));
                    phaseBHasEvent = phaseBGen.MoveNext();
                }

                engine.ForceStep(nowUs);
                lastNowUs = nowUs;
            }

            for (int i = 0; i < 2000; i++)
            {
                long nowUs = lastNowUs + (i + 1) * stepIntervalUs;
                engine.ForceStep(nowUs);
            }

            Assert.NotEmpty(estimates);

            Assert.True(phaseAEndUs > 0, "Did not reach calm state before applying step.");

            var preStepTail = estimates.Where(e => e.NowUs <= phaseAEndUs).TakeLast(3).ToList();
            Assert.NotEmpty(preStepTail);

            var preStep = estimates.Last(e => e.NowUs <= phaseAEndUs);
            Assert.Equal("Track", preStep.State);
            Assert.NotEqual("Degraded", preStep.State);

            int firstPhaseBIndex = estimates.FindIndex(e => e.NowUs >= stepStartUs);
            Assert.InRange(firstPhaseBIndex, 0, estimates.Count - 1);

            int maxEstimatesAfterStep = 10;
            long maxUsAfterStep = 6_000_000;

            int firstHoldIndex = estimates.FindIndex(firstPhaseBIndex, e => e.State == "Hold");
            bool holdSoonEnough =
                firstHoldIndex >= 0 &&
                (firstHoldIndex - firstPhaseBIndex) <= maxEstimatesAfterStep &&
                (estimates[firstHoldIndex].NowUs - stepStartUs) <= maxUsAfterStep;

            if (!holdSoonEnough)
            {
                var statesAfterStep = string.Join("; ", estimates
                    .Skip(firstPhaseBIndex)
                    .Take(8)
                    .Select(e => $"t={e.NowUs} state={e.State}"));

                throw new XunitException(
                    "Expected Hold entry shortly after rate step." + Environment.NewLine +
                    $"stepStartUs={stepStartUs} statesAfterStep=[{statesAfterStep}]" + Environment.NewLine +
                    DumpTail(trace));
            }

            Assert.True(
                firstHoldIndex + 1 < estimates.Count &&
                estimates[firstHoldIndex + 1].State == "Hold",
                "Hold should persist across multiple estimate ticks.");

            long firstHoldNowUs = estimates[firstHoldIndex].NowUs;

            int degradedAfterStep = estimates.FindIndex(firstHoldIndex, e => e.State == "Degraded");
            int trackAfterHoldIndex = estimates.FindIndex(firstHoldIndex, e => e.State == "Track");

            if (degradedAfterStep >= 0 && (trackAfterHoldIndex < 0 || degradedAfterStep < trackAfterHoldIndex))
            {
                if (!(degradedAfterStep + 1 < estimates.Count && estimates[degradedAfterStep + 1].State == "Degraded"))
                {
                    throw new XunitException(
                        "Degraded should persist across multiple estimate ticks after safety preemption." + Environment.NewLine +
                        DumpTail(trace));
                }

                return;
            }

            if (trackAfterHoldIndex < 0 || estimates[trackAfterHoldIndex].NowUs - firstHoldNowUs > 30_000_000)
            {
                throw new XunitException(
                    "Expected engine to return to Track after quiet horizon." + Environment.NewLine +
                    DumpTail(trace));
            }
        }

        [Fact]
        public void RateStep_ExtremeTransient_CanEnterDegraded_AndPreemptsTrackRecovery()
        {
            var gateLadderUs = new[] { 500, 1000, 2000, 4000 };

            using var engine = new AdaptiveWindowEngine(
                baseDeltaUs: 50,
                gateLadderUs: gateLadderUs,
                windowStartSec: 1.0,
                windowMinSec: 1.0,
                windowMaxSec: 6.0,
                zMin: 1,
                epsY: 0.20,
                epsM1: 0.10,
                startWorker: false,
                enableFileLog: false);

            engine.Beta = 0.10;
            engine.ZPoisson = 4.0;
            engine.ZTrack = 4.0;
            engine.ZHold = 6.0;
            engine.MinGateCountForZ = 2;

            var estimates = new List<AdaptiveWindowEngine.Estimate>();
            var trace = new List<(long tUs, string state, int gateUs, bool hasSignificance, double y, double zY, double windowSec)>();
            engine.OnEstimate += est =>
            {
                estimates.Add(est);
                trace.Add((est.NowUs, est.State, est.GateUs, est.HasSignificance, est.Y, est.ZY, est.WindowSec));
            };

            double phaseAParentRateCps = 200.0;
            double phaseAWindowSec = 20.0;
            double phaseBDurationSec = 12.0;
            double phaseBParentRateCps = 2000.0;
            int meanClusterSize = 4;
            double childMeanDelayUs = 200.0;

            long stepIntervalUs = 20_000; // 20 ms
            long phaseAEndUs = 0;

            int calmNeeded = 2;
            int calmCount = 0;

            var phaseAGen = GenerateClusteredSegment(seed: 98765, startUs: 0, durationSec: phaseAWindowSec, parentRateCps: phaseAParentRateCps, meanClusterSize: meanClusterSize, childMeanDelayUs: childMeanDelayUs)
                .GetEnumerator();
            bool phaseAHasEvent = phaseAGen.MoveNext();

            long phaseAWindowEndUs = (long)(phaseAWindowSec * 1e6);
            long lastNowUs = 0;

            for (long nowUs = stepIntervalUs; nowUs <= phaseAWindowEndUs; nowUs += stepIntervalUs)
            {
                while (phaseAHasEvent && phaseAGen.Current <= nowUs)
                {
                    engine.OnDetection(new Detection(phaseAGen.Current, 0));
                    phaseAHasEvent = phaseAGen.MoveNext();
                }

                int prevCount = estimates.Count;
                engine.ForceStep(nowUs);
                lastNowUs = nowUs;

                if (estimates.Count > prevCount)
                {
                    var latestEstimate = estimates[^1];

                    bool isSettled = latestEstimate.State == "Track";
                    if (isSettled)
                    {
                        calmCount++;
                        if (calmCount >= calmNeeded)
                        {
                            phaseAEndUs = latestEstimate.NowUs;
                            break;
                        }
                    }
                    else
                    {
                        calmCount = 0;
                    }
                }
            }

            if (phaseAEndUs == 0)
            {
                throw new XunitException(
                    "Did not reach calm state before applying step." + Environment.NewLine +
                    DumpTail(trace));
            }

            var phaseBGen = GenerateClusteredSegment(seed: 86420, startUs: phaseAEndUs, durationSec: phaseBDurationSec, parentRateCps: phaseBParentRateCps, meanClusterSize: meanClusterSize, childMeanDelayUs: childMeanDelayUs)
                .GetEnumerator();
            bool phaseBHasEvent = phaseBGen.MoveNext();

            long phaseBEndUs = phaseAEndUs + (long)(phaseBDurationSec * 1e6);

            for (long nowUs = phaseAEndUs + stepIntervalUs; nowUs <= phaseBEndUs; nowUs += stepIntervalUs)
            {
                while (phaseBHasEvent && phaseBGen.Current <= nowUs)
                {
                    engine.OnDetection(new Detection(phaseBGen.Current, 0));
                    phaseBHasEvent = phaseBGen.MoveNext();
                }

                engine.ForceStep(nowUs);
                lastNowUs = nowUs;
            }

            for (int i = 0; i < 2000; i++)
            {
                long nowUs = lastNowUs + (i + 1) * stepIntervalUs;
                engine.ForceStep(nowUs);
            }

            Assert.NotEmpty(estimates);

            Assert.True(phaseAEndUs > 0, "Did not reach calm state before applying step.");

            var preStepTail = estimates.Where(e => e.NowUs <= phaseAEndUs).TakeLast(3).ToList();
            Assert.NotEmpty(preStepTail);

            var preStep = estimates.Last(e => e.NowUs <= phaseAEndUs);
            Assert.Equal("Track", preStep.State);
            Assert.NotEqual("Degraded", preStep.State);

            int firstPhaseBIndex = estimates.FindIndex(e => e.NowUs >= phaseAEndUs);
            Assert.InRange(firstPhaseBIndex, 0, estimates.Count - 1);

            int maxEstimatesAfterStep = 15;
            long maxUsAfterStep = 12_000_000;

            int firstHoldIndex = estimates.FindIndex(firstPhaseBIndex, e => e.State == "Hold");
            bool holdSoonEnough =
                firstHoldIndex >= 0 &&
                (firstHoldIndex - firstPhaseBIndex) <= maxEstimatesAfterStep &&
                (estimates[firstHoldIndex].NowUs - phaseAEndUs) <= maxUsAfterStep;

            if (!holdSoonEnough)
            {
                throw new XunitException(
                    "Expected Hold entry shortly after rate step." + Environment.NewLine +
                    DumpTail(trace));
            }

            Assert.True(
                firstHoldIndex + 1 < estimates.Count &&
                estimates[firstHoldIndex + 1].State == "Hold",
                "Hold should persist across multiple estimate ticks.");

            int degradedAfterStep = estimates.FindIndex(firstPhaseBIndex, e => e.State == "Degraded");
            if (degradedAfterStep < 0)
            {
                throw new XunitException(
                    "Expected Degraded state after violent transient." + Environment.NewLine +
                    DumpTail(trace));
            }

            Assert.True(
                degradedAfterStep + 1 < estimates.Count &&
                estimates[degradedAfterStep + 1].State == "Degraded",
                "Degraded should persist across multiple estimate ticks.");

            int lookahead = Math.Min(5, estimates.Count - degradedAfterStep - 1);
            for (int i = 1; i <= lookahead; i++)
            {
                Assert.NotEqual("Track", estimates[degradedAfterStep + i].State);
            }
        }
    }
}
