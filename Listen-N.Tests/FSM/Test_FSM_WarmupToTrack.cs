using System;
using System.Reflection;
using Listen_N;
using Xunit;

namespace AdaptiveWindowTests.FSM
{
    public class Test_FSM_WarmupToTrack
    {
        [Fact]
        public void WarmupTransitionsToTrackAfterTwoConfirmations()
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
            var zTrackField = engineType.GetField("_zTrack", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("_zTrack field not found via reflection.");
            var confirmationsField = engineType.GetField("_fsmConfirmations", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("_fsmConfirmations field not found via reflection.");

            var warmupState = Enum.Parse(fsmType, "Warmup");
            var trackState = Enum.Parse(fsmType, "Track");

            enterState.Invoke(engine, new object[] { warmupState, 0L });

            double zTrack = (double)(zTrackField.GetValue(engine) ?? throw new InvalidOperationException("_zTrack value unavailable."));

            void Step(long nowUs, double zy)
            {
                double sigmaY = 1.0 / Math.Max(zy, 1e-12);
                adaptState.Invoke(
                    engine,
                    new object[]
                    {
                        nowUs,
                        1.0,
                        sigmaY,
                        5.0,
                        1.0,
                        3,
                        true,
                        false,
                        false,
                        false,
                        zy
                    });
            }

            // Step 1: ZY below track threshold; state should remain Warmup with no pending transition.
            Step(1_000_000L, 0.5 * zTrack);

            Assert.Equal(warmupState, fsmField.GetValue(engine));
            Assert.Equal(warmupState, pendingFsmField.GetValue(engine));
            Assert.Equal(0, confirmationsField.GetValue(engine));

            // Step 2: ZY exceeds track threshold; should create pending Track transition with one confirmation.
            Step(2_000_000L, 1.1 * zTrack);

            Assert.Equal(warmupState, fsmField.GetValue(engine));
            Assert.Equal(trackState, pendingFsmField.GetValue(engine));
            Assert.Equal(1, confirmationsField.GetValue(engine));

            // Step 3: Second confirmation should finalize transition to Track and reset confirmations.
            Step(3_000_000L, 1.1 * zTrack);

            Assert.Equal(trackState, fsmField.GetValue(engine));
            Assert.Equal(trackState, pendingFsmField.GetValue(engine));
            Assert.Equal(0, confirmationsField.GetValue(engine));
        }
    }
}
