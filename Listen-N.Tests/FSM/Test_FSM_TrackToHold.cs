using System;
using System.Reflection;
using Listen_N;
using Xunit;

namespace AdaptiveWindowTests.FSM
{
    public class Test_FSM_TrackToHold
    {
        [Fact]
        public void TrackTransitionsToHoldAfterTwoConfirmationsWhenAlarmPersists()
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
            var phAlarmField = engineType.GetField("_phAlarm", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("_phAlarm field not found via reflection.");

            var zPoissonField = engineType.GetField("_zPoisson", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("_zPoisson field not found via reflection.");
            var quietField = engineType.GetField("_poissonQuietStreak", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("_poissonQuietStreak field not found via reflection.");
            var insufficientStatsField = engineType.GetField("_insufficientStatistics", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("_insufficientStatistics field not found via reflection.");

            var trackState = Enum.Parse(fsmType, "Track");
            var holdState = Enum.Parse(fsmType, "Hold");

            enterState.Invoke(engine, new object[] { trackState, 0L });

            zPoissonField.SetValue(engine, 0.0);
            quietField.SetValue(engine, 0);
            insufficientStatsField.SetValue(engine, false);

            void Step(long nowUs)
            {
                phAlarmField.SetValue(engine, true);
                adaptState.Invoke(
                    engine,
                    new object[]
                    {
                        nowUs,
                        1.0,
                        0.1,
                        5.0,
                        1.0,
                        5,
                        true,
                        false,
                        false,
                        false,
                        10.0
                    });
            }

            Step(1_000_000L);

            Assert.Equal(trackState, fsmField.GetValue(engine));
            Assert.Equal(holdState, pendingFsmField.GetValue(engine));
            Assert.Equal(1, confirmationsField.GetValue(engine));

            Step(2_000_000L);

            Assert.Equal(holdState, fsmField.GetValue(engine));
            Assert.Equal(holdState, pendingFsmField.GetValue(engine));
            Assert.Equal(0, confirmationsField.GetValue(engine));
        }
    }
}
