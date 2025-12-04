using System;
using System.Reflection;
using Listen_N;
using Xunit;

namespace AdaptiveWindowTests.FSM
{
    public class Test_FSM_HoldToTrack
    {
        [Fact]
        public void HoldTransitionsBackToTrackAfterQuietPeriodExpiresAndTwoConfirmations()
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
            var holdQuietField = engineType.GetField("_holdQuietUntilUs", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("_holdQuietUntilUs field not found via reflection.");

            var zPoissonField = engineType.GetField("_zPoisson", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("_zPoisson field not found via reflection.");
            var quietField = engineType.GetField("_poissonQuietStreak", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("_poissonQuietStreak field not found via reflection.");
            var insufficientStatsField = engineType.GetField("_insufficientStatistics", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("_insufficientStatistics field not found via reflection.");

            var holdState = Enum.Parse(fsmType, "Hold");
            var trackState = Enum.Parse(fsmType, "Track");

            enterState.Invoke(engine, new object[] { holdState, 0L });

            // Prevent other FSM paths from activating during the test
            zPoissonField.SetValue(engine, 0.0);
            quietField.SetValue(engine, 0);
            insufficientStatsField.SetValue(engine, false);

            // Ensure the quiet period covers the first step and expires before the second.
            holdQuietField.SetValue(engine, 1_500_000L);

            void Adapt(long nowUs)
            {
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

            // STEP 1: Quiet period has not expired; FSM should remain in Hold with no pending transition.
            Adapt(1_000_000L);

            Assert.Equal(holdState, fsmField.GetValue(engine));
            Assert.Equal(holdState, pendingFsmField.GetValue(engine));
            Assert.Equal(0, confirmationsField.GetValue(engine));

            // STEP 2: Quiet period has expired; create pending Track transition with one confirmation.
            Adapt(2_000_000L);

            Assert.Equal(holdState, fsmField.GetValue(engine));
            Assert.Equal(trackState, pendingFsmField.GetValue(engine));
            Assert.Equal(1, confirmationsField.GetValue(engine));

            // STEP 3: Second confirmation finalizes transition to Track.
            Adapt(3_000_000L);

            Assert.Equal(trackState, fsmField.GetValue(engine));
            Assert.Equal(trackState, pendingFsmField.GetValue(engine));
            Assert.Equal(0, confirmationsField.GetValue(engine));
        }
    }
}
