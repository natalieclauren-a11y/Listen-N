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
        private static IEnumerable<long> GeneratePiecewisePoisson(int seed, params (double durationSec, double rateCps)[] segments)
        {
            var rng = new Random(seed);
            double tSec = 0.0;
            long lastUs = -1;

            foreach (var segment in segments)
            {
                double endSec = tSec + segment.durationSec;
                while (tSec < endSec)
                {
                    double u;
                    do
                    {
                        u = rng.NextDouble();
                    }
                    while (u <= 0.0 || u >= 1.0);

                    double dtSec = -Math.Log(1 - u) / segment.rateCps;
                    tSec += dtSec;
                    if (tSec > endSec)
                    {
                        break;
                    }

                    long tsUs = (long)Math.Round(tSec * 1e6);
                    if (tsUs <= lastUs)
                    {
                        tsUs = lastUs + 1;
                    }

                    yield return tsUs;
                    lastUs = tsUs;
                }
            }
        }

        private static string DumpTail(IEnumerable<(long tUs, string state, int gateUs)> trace, int count = 20)
        {
            var tail = trace.TakeLast(Math.Min(count, trace.Count()))
                .Select(entry => $"t={entry.tUs / 1_000_000.0:F3}s state={entry.state} gate={entry.gateUs}");
            return string.Join("; ", tail);
        }

        [Fact]
        public void RateStep_TriggersHold_ThenReturnsToTrackAfterQuietHorizon()
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
            var trace = new List<(long tUs, string state, int gateUs)>();
            engine.OnEstimate += est =>
            {
                estimates.Add(est);
                trace.Add((est.NowUs, est.State, est.GateUs));
            };

            var segments = new[]
            {
                (durationSec: 4.0, rateCps: 500.0),
                (durationSec: 6.0, rateCps: 5000.0),
                (durationSec: 10.0, rateCps: 5000.0)
            };

            var timestamps = GeneratePiecewisePoisson(seed: 13579, segments: segments).ToList();
            Assert.NotEmpty(timestamps);

            long totalDurationUs = (long)(segments.Sum(s => s.durationSec) * 1e6);
            long stepIntervalUs = 20_000; // 20 ms
            int eventIndex = 0;

            for (long nowUs = stepIntervalUs; nowUs <= totalDurationUs; nowUs += stepIntervalUs)
            {
                while (eventIndex < timestamps.Count && timestamps[eventIndex] <= nowUs)
                {
                    engine.OnDetection(new Detection(timestamps[eventIndex], 0));
                    eventIndex++;
                }

                engine.ForceStep(nowUs);
            }

            for (int i = 0; i < 200; i++)
            {
                long nowUs = totalDurationUs + (i + 1) * stepIntervalUs;
                engine.ForceStep(nowUs);
            }

            Assert.NotEmpty(estimates);

            long phaseAEndUs = (long)(segments[0].durationSec * 1e6);

            long preStepWindowUs = 1_000_000; // 1 s
            long preStepStartUs = Math.Max(0, phaseAEndUs - preStepWindowUs);

            var preStepEstimates = estimates
                .Where(e => e.NowUs >= preStepStartUs && e.NowUs < phaseAEndUs)
                .ToList();
            Assert.NotEmpty(preStepEstimates);

            Assert.DoesNotContain(estimates.Where(e => e.NowUs < phaseAEndUs), e => e.State == "Degraded");

            Assert.DoesNotContain(preStepEstimates, e => e.State == "Hold" || e.State == "Degraded");

            int firstPhaseBIndex = estimates.FindIndex(e => e.NowUs >= phaseAEndUs);
            Assert.InRange(firstPhaseBIndex, 0, estimates.Count - 1);

            int lastHoldBeforeStep = estimates.FindLastIndex(e => e.NowUs < phaseAEndUs && e.State == "Hold");

            int firstHoldIndex = estimates.FindIndex(firstPhaseBIndex, e => e.State == "Hold");
            if (firstHoldIndex < 0 || estimates[firstHoldIndex].NowUs - phaseAEndUs > 2_000_000)
            {
                throw new XunitException(
                    "Expected Hold entry shortly after rate step." + Environment.NewLine +
                    DumpTail(trace));
            }

            Assert.True(firstHoldIndex > lastHoldBeforeStep, "Hold after step should be a new episode.");

            Assert.True(
                firstHoldIndex + 1 < estimates.Count &&
                estimates[firstHoldIndex + 1].State == "Hold",
                "Hold should persist across multiple estimate ticks.");

            long firstHoldNowUs = estimates[firstHoldIndex].NowUs;

            int trackAfterHoldIndex = estimates.FindIndex(firstHoldIndex, e => e.State == "Track");
            if (trackAfterHoldIndex < 0 || estimates[trackAfterHoldIndex].NowUs - firstHoldNowUs > 30_000_000)
            {
                throw new XunitException(
                    "Expected engine to return to Track after quiet horizon." + Environment.NewLine +
                    DumpTail(trace));
            }

            var holdGates = trace.Where(entry => entry.state == "Hold").Select(entry => entry.gateUs).ToList();
            if (holdGates.Count >= 2)
            {
                Assert.True(holdGates.All(g => g == holdGates[0]), "Gate should remain frozen during Hold.");
            }
        }
    }
}
