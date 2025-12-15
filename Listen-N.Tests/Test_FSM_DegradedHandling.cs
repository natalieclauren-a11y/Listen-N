using System;
using System.Reflection;
using Listen_N;
using Xunit;

namespace AdaptiveWindowTests
{
    public class Test_FSM_DegradedHandling
    {
        [Fact]
        public void ModelMismatchTransitionsToDegraded()
        {
            var engine = new AdaptiveWindowEngine(startWorker: false);
            engine.EpsY = 1e-6; // make model mismatch triggers deterministic

            var engineType = typeof(AdaptiveWindowEngine);
            var fsmType = engineType.GetNestedType("FSM", BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("FSM enum not found via reflection.");

            var fsmField = engineType.GetField("_fsm", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("_fsm field not found via reflection.");
            var pendingFsmField = engineType.GetField("_pendingFsm", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("_pendingFsm field not found via reflection.");
            var mismatchFlagField = engineType.GetField("_modelMismatch", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("_modelMismatch field not found via reflection.");
            var mismatchRequiredField = engineType.GetField("_mismatchStreakRequired", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("_mismatchStreakRequired field not found via reflection.");

            var trackState = Enum.Parse(fsmType, "Track");
            var degradedState = Enum.Parse(fsmType, "Degraded");

            // Start from Track so the Warmup heuristics do not override the mismatch-driven transition.
            fsmField.SetValue(engine, trackState);
            pendingFsmField.SetValue(engine, trackState);

            int mismatchRequired = (int)(mismatchRequiredField.GetValue(engine)
                ?? throw new InvalidOperationException("_mismatchStreakRequired value missing."));

            long nowUs = 600_000;
            for (int i = 0; i < mismatchRequired; i++)
            {
                mismatchFlagField.SetValue(engine, true);
                engine.ForceStep(nowUs);
                nowUs += 200_000;
            }

            Assert.Equal(degradedState, pendingFsmField.GetValue(engine));

            mismatchFlagField.SetValue(engine, true);
            engine.ForceStep(nowUs);
            nowUs += 200_000;

            Assert.Equal(degradedState, fsmField.GetValue(engine));

            mismatchFlagField.SetValue(engine, true);
            engine.ForceStep(nowUs);

            Assert.Equal(degradedState, fsmField.GetValue(engine));
        }
    }
}
