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
            var zPoissonField = engineType.GetField("_zPoisson", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("_zPoisson field not found via reflection.");
            var poissonQuietStreakField = engineType.GetField("_poissonQuietStreak", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("_poissonQuietStreak field not found via reflection.");
            var poissonQuietRequiredField = engineType.GetField("_poissonQuietRequired", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("_poissonQuietRequired field not found via reflection.");

            var warmupState = Enum.Parse(fsmType, "Warmup");
            var lowRateState = Enum.Parse(fsmType, "LowRate");
            var poissonState = Enum.Parse(fsmType, "Poisson");
            var trackState = Enum.Parse(fsmType, "Track");
            var holdState = Enum.Parse(fsmType, "Hold");

            enterState.Invoke(engine, new object[] { warmupState, 0L });

            zPoissonField.SetValue(engine, 0.0);
            poissonQuietStreakField.SetValue(engine, 0);
            poissonQuietRequiredField.SetValue(engine, 999);

            void Step(long nowUs)
            {
                adaptState.Invoke(
                    engine,
                    new object[]
                    {
                        nowUs,
                        0.0,
                        1.0,
                        0.0,
                        0.0,
                        0,
                        false,
                        false,
                        false,
                        false,
                        0.0
                    });
            }

            Step(1_000_000L);

            Assert.Equal(warmupState, fsmField.GetValue(engine));
            Assert.Equal(lowRateState, pendingFsmField.GetValue(engine));
            Assert.Equal(1, confirmationsField.GetValue(engine));
            Assert.NotEqual(poissonState, pendingFsmField.GetValue(engine));
            Assert.NotEqual(trackState, pendingFsmField.GetValue(engine));
            Assert.NotEqual(holdState, pendingFsmField.GetValue(engine));

            Step(2_000_000L);

            Assert.Equal(lowRateState, fsmField.GetValue(engine));
            Assert.Equal(lowRateState, pendingFsmField.GetValue(engine));
            Assert.Equal(0, confirmationsField.GetValue(engine));
            Assert.NotEqual(poissonState, fsmField.GetValue(engine));
            Assert.NotEqual(trackState, fsmField.GetValue(engine));
            Assert.NotEqual(holdState, fsmField.GetValue(engine));
        }
    }
}
