using System;
using System.Reflection;
using Listen_N;
using Xunit;

namespace AdaptiveWindowTests.FSM
{
    public class Test_FSM_PoissonDetection
    {
        [Fact]
        public void WarmupTransitionsToPoissonAfterQuietStreak()
        {
            var engine = new AdaptiveWindowEngine(startWorker: false);

            var engineType = typeof(AdaptiveWindowEngine);
            var fsmType = engineType.GetNestedType("FSM", BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("FSM enum not found via reflection.");

            var enterState = engineType.GetMethod("EnterState", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("EnterState method not found via reflection.");
            var adaptState = engineType.GetMethod("AdaptState", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("AdaptState method not found via reflection.");

            var fsmField = engineType.GetField("_fsm", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("_fsm field not found via reflection.");
            var pendingFsmField = engineType.GetField("_pendingFsm", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("_pendingFsm field not found via reflection.");
            var confirmationsField = engineType.GetField("_fsmConfirmations", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("_fsmConfirmations field not found via reflection.");

            var quietStreakField = engineType.GetField("_poissonQuietStreak", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("_poissonQuietStreak field not found via reflection.");
            var quietRequiredField = engineType.GetField("_poissonQuietRequired", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("_poissonQuietRequired field not found via reflection.");
            var zPoissonField = engineType.GetField("_zPoisson", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("_zPoisson field not found via reflection.");

            var warmupState = Enum.Parse(fsmType, "Warmup");
            var poissonState = Enum.Parse(fsmType, "Poisson");

            // Start in Warmup deterministically
            enterState.Invoke(engine, new object[] { warmupState, 0L });

            // Ensure "windowFilled" can become true by anchoring accumulator left edge at 0.
            var accField = engineType.GetField("_acc", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("_acc field not found via reflection.");
            var accInstance = accField.GetValue(engine) ?? throw new InvalidOperationException("_acc value unavailable.");
            var accType = accInstance.GetType();

            var t0Field = accType.GetField("_t0Us", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("_t0Us field not found via reflection.");
            t0Field.SetValue(accInstance, 0L);

            // Force accumulator to appear non-empty so positiveM1 = true (Warmup requires m1 > 0).
            var totalField = accType.GetField("_total", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("_total field not found via reflection.");
            totalField.SetValue(accInstance, 10);

            // Start quiet streak from zero
            quietStreakField.SetValue(engine, 0);

            // Make the test deterministic: require a multi-step quiet streak
            quietRequiredField.SetValue(engine, 3);

            double zPoisson = (double)(zPoissonField.GetValue(engine)
                ?? throw new InvalidOperationException("_zPoisson value unavailable."));
            Assert.True(0.1 < zPoisson, "maxAbsZ must stay below zPoisson for Poisson detection.");

            void Step(long nowUs)
            {
                // AdaptState(nowUs, Y, sigY, m1, varM1, gatesUsed, hasAnySignificance, singlesChange, correlationChange, degraded, maxAbsZ)
                adaptState.Invoke(
                    engine,
                    new object[]
                    {
                        nowUs,
                        0.1,   // Y
                        1.0,   // sigY -> Z = 0.1
                        5.0,   // m1 (used by Warmup positiveM1 check)
                        1.0,   // varM1
                        5,     // gatesUsed (>= 2)
                        true,  // hasAnySignificance (not used in Warmup, but keep "usable" semantics)
                        false, // singlesChange
                        false, // correlationChange
                        false, // degraded
                        0.1    // maxAbsZ
                    });
            }

            // Step 1: quiet streak begins, remain in Warmup
            Step(2_000_000L);
            Assert.Equal(warmupState, fsmField.GetValue(engine));
            Assert.Equal(1, quietStreakField.GetValue(engine));

            // Step 2: quiet streak continues, remain in Warmup
            Step(3_000_000L);
            Assert.Equal(warmupState, fsmField.GetValue(engine));
            Assert.Equal(2, quietStreakField.GetValue(engine));

            // Step 3: quiet streak reaches threshold; Poisson is requested (pending), but FSM transitions require 2 confirmations
            Step(4_000_000L);
            Assert.Equal(warmupState, fsmField.GetValue(engine));
            Assert.Equal(poissonState, pendingFsmField.GetValue(engine));
            Assert.Equal(1, confirmationsField.GetValue(engine));

            // Step 4: second confirmation finalizes the transition into Poisson
            Step(5_000_000L);
            Assert.Equal(poissonState, fsmField.GetValue(engine));
            Assert.Equal(poissonState, pendingFsmField.GetValue(engine));
            Assert.Equal(0, confirmationsField.GetValue(engine));
        }
    }
}
