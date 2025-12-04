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

            enterState.Invoke(engine, new object[] { warmupState, 0L });

            // Ensure the accumulator window appears filled by anchoring its left edge at zero.
            var accField = engineType.GetField("_acc", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("_acc field not found via reflection.");
            var accInstance = accField.GetValue(engine) ?? throw new InvalidOperationException("_acc value unavailable.");
            var accType = accInstance.GetType();
            var t0Field = accType.GetField("_t0Us", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("_t0Us field not found via reflection.");
            t0Field.SetValue(accInstance, 0L);

            // Reset quiet streak to start counting from zero.
            quietStreakField.SetValue(engine, 0);

            double zPoisson = (double)(zPoissonField.GetValue(engine)
                ?? throw new InvalidOperationException("_zPoisson value unavailable."));
            int quietRequired = (int)(quietRequiredField.GetValue(engine)
                ?? throw new InvalidOperationException("_poissonQuietRequired value unavailable."));

            void Step(long nowUs)
            {
                adaptState.Invoke(
                    engine,
                    new object[]
                    {
                        nowUs,
                        0.1,
                        1.0,
                        5.0,
                        1.0,
                        5,
                        false,
                        false,
                        false,
                        false,
                        0.1
                    });
            }

            Assert.True(0.1 < zPoisson, "maxAbsZ must stay below zPoisson to avoid unintended transitions.");
            Assert.True(quietRequired >= 1, "Quiet streak requirement should be at least one step.");

            // STEP 1: first quiet observation inside Warmup.
            Step(2_000_000L);
            Assert.Equal(warmupState, fsmField.GetValue(engine));
            Assert.Equal(1, quietStreakField.GetValue(engine));

            // STEP 2: second quiet observation should extend the quiet streak without leaving Warmup yet.
            Step(3_000_000L);
            Assert.Equal(warmupState, fsmField.GetValue(engine));
            Assert.Equal(2, quietStreakField.GetValue(engine));

            // STEP 3: after sufficient quiet streak, Warmup should transition into Poisson.
            Step(4_000_000L);
            Assert.Equal(poissonState, fsmField.GetValue(engine));
            Assert.Equal(poissonState, pendingFsmField.GetValue(engine));
            Assert.Equal(0, confirmationsField.GetValue(engine));
        }
    }
}
