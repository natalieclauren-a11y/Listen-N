using System;
using System.Reflection;
using Listen_N;
using Xunit;

namespace AdaptiveWindowTests.FSM
{
    public class Test_FSM_WarmupToLowRate
    {
        [Fact]
        public void WarmupTransitionsToLowRateAfterTwoLowRateSamples()
        {
            var engine = new AdaptiveWindowEngine(startWorker: false);

            var engineType = typeof(AdaptiveWindowEngine);
            var fsmType = engineType.GetNestedType("FSM", BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("FSM nested enum not found.");

            object warmupState = Enum.Parse(fsmType, "Warmup");
            object lowRateState = Enum.Parse(fsmType, "LowRate");
            object trackState = Enum.Parse(fsmType, "Track");
            object holdState = Enum.Parse(fsmType, "Hold");
            object poissonState = Enum.Parse(fsmType, "Poisson");

            var fsmField = engineType.GetField("_fsm", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("_fsm field not found via reflection.");
            var pendingFsmField = engineType.GetField("_pendingFsm", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("_pendingFsm field not found via reflection.");
            var confirmationsField = engineType.GetField("_fsmConfirmations", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("_fsmConfirmations field not found via reflection.");

            // Poisson guard rails (we do NOT want "quiet" to steal this test)
            var zPoissonField = engineType.GetField("_zPoisson", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("_zPoisson field not found via reflection.");
            var poissonQuietStreakField = engineType.GetField("_poissonQuietStreak", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("_poissonQuietStreak field not found via reflection.");
            var poissonQuietRequiredField = engineType.GetField("_poissonQuietRequired", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("_poissonQuietRequired field not found via reflection.");

            // Step scheduling introspection (optional but makes failures obvious)
            var nextStepUsField = engineType.GetField("_nextStepUs", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("_nextStepUs field not found via reflection.");

            // Force Poisson to be effectively unreachable in this test
            zPoissonField.SetValue(engine, 99);
            poissonQuietStreakField.SetValue(engine, 0);
            poissonQuietRequiredField.SetValue(engine, 999);

            // Confirm initial state is Warmup and pending is Warmup (or null)
            Assert.Equal(warmupState, fsmField.GetValue(engine));

            // Sample 1: no detections -> RunAnalysis should request LowRate (pending).
            long before1 = (long)(nextStepUsField.GetValue(engine) ?? 0L);
            engine.ForceStep(1_000_000L);
            long after1 = (long)(nextStepUsField.GetValue(engine) ?? 0L);

            Assert.True(after1 != 0 || before1 != 0, $"ForceStep should schedule steps. _nextStepUs before={before1}, after={after1}.");

            var fsm1 = fsmField.GetValue(engine);
            var pending1 = pendingFsmField.GetValue(engine);
            var conf1 = (int)(confirmationsField.GetValue(engine) ?? 0);

            // After first low-rate sample: either still Warmup with 1 confirmation,
            // or already LowRate if confirmation occurred within the same tick.
            Assert.True(Equals(fsm1, warmupState) || Equals(fsm1, lowRateState),
                $"Expected Warmup or LowRate after first sample, got {fsm1}.");
            Assert.Equal(lowRateState, pending1);
            if (Equals(fsm1, warmupState)) Assert.Equal(1, conf1);

            Assert.NotEqual(poissonState, pending1);
            Assert.NotEqual(trackState, pending1);
            Assert.NotEqual(holdState, pending1);

            // Sample 2: second confirmation should finalize LowRate.
            engine.ForceStep(2_000_000L);

            Assert.Equal(lowRateState, fsmField.GetValue(engine));
            Assert.Equal(lowRateState, pendingFsmField.GetValue(engine));
            Assert.Equal(0, confirmationsField.GetValue(engine));
            Assert.NotEqual(poissonState, fsmField.GetValue(engine));
            Assert.NotEqual(trackState, fsmField.GetValue(engine));
            Assert.NotEqual(holdState, fsmField.GetValue(engine));
        }
    }
}
