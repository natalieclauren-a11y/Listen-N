using System;
using System.Linq;
using Listen_N;
using Xunit;

namespace Listen_N.Tests
{
    public sealed class RtWindowSummaryTests
    {
        [Fact]
        public void WindowSummary_HasExpectedShapeAndRateConsistency()
        {
            using var engine = new AdaptiveWindowEngine(
                baseDeltaUs: 50,
                windowStartSec: 1.0,
                windowMinSec: 1.0,
                windowMaxSec: 10.0,
                gateLadderUs: new[] { 500, 1000 },
                zMin: 1,
                epsY: 0.10,
                epsM1: 0.02,
                startWorker: false,
                enableFileLog: false);

            Integrated.Contracts.RtWindowSummary? captured = null;
            engine.OnWindowSummary += summary => captured = summary;

            for (int det = 0; det < 15; det++)
            {
                long tUs = 10_000L * (det + 1);
                engine.OnDetection(new Detection(tUs, (byte)det));
            }

            engine.ForceStep(1_000_000L);

            Assert.NotNull(captured);
            Assert.Equal(15, captured!.Counts15.Length);
            Assert.True(captured.DurationSeconds > 0);

            double totalCounts = captured.Counts15.Sum();
            if (captured.RateTotalCps > 0 && captured.DurationSeconds > 0)
            {
                double expected = captured.RateTotalCps * captured.DurationSeconds;
                Assert.InRange(totalCounts, expected - 1e-6, expected + 1e-6);
            }
        }
    }
}
