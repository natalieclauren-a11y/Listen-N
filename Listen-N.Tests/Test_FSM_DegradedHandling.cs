using System.Collections.Generic;
using System.Linq;
using Listen_N;
using Xunit;

namespace AdaptiveWindowTests
{
    public class Test_FSM_DegradedHandling
    {
        [Fact]
        public void ModelMismatchTransitionsToDegraded()
        {
            using var engine = new AdaptiveWindowEngine(startWorker: false);
            engine.EpsY = 1e-6; // make model mismatch triggers deterministic

            var estimates = new List<AdaptiveWindowEngine.Estimate>();
            engine.OnEstimate += est => estimates.Add(est);

            var timestamps = SyntheticTimestampGenerator
                .CorrelatedBurstSource(seed: 2024, durationSec: 8.0)
                .ToList();

            Assert.NotEmpty(timestamps);

            engine.ResetTimestampState(timestamps[0]);

            foreach (var t in timestamps)
            {
                engine.OnDetection(new Detection(t));
                engine.ForceStep(t);
            }

            engine.ForceStep(timestamps[^1] + 2_000_000);

            Assert.Contains(estimates, est => est.ModelMismatch);
            Assert.Contains(estimates, est => est.State == "Degraded");
        }
    }
}
