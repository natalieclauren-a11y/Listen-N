using System;
using System.Reflection;
using Listen_N;
using Xunit;

namespace AdaptiveWindowTests
{
    public class Test_InitializeNextStep
    {
        [Fact]
        public void ForceStepSchedulesAfterResetAtZero()
        {
            using var engine = new AdaptiveWindowEngine(startWorker: false);

            engine.ResetTimestampState(0);
            engine.ForceStep(1_000_000);

            var engineType = typeof(AdaptiveWindowEngine);
            var nextStepField = engineType.GetField("_nextStepUs", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("_nextStepUs field not found via reflection.");

            long nextStepUs = (long)(nextStepField.GetValue(engine) ?? 0L);

            Assert.NotEqual(0L, nextStepUs);
        }
    }
}
