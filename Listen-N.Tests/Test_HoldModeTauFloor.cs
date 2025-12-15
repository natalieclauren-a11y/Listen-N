using System;
using System.Reflection;
using Listen_N;
using Xunit;

namespace AdaptiveWindowTests
{
    public class Test_HoldModeTauFloor
    {
        [Fact]
        public void Test_HoldMode_EnforcesTauFloor()
        {
            using var engine = new AdaptiveWindowEngine(
                windowStartSec: 1.0,
                windowMinSec: 0.1,
                windowMaxSec: 60.0,
                startWorker: false,
                enableFileLog: false);

            // Physics-informed estimate of the correlation time (tau-hat).
            // The tau-floor constraint requires the integration window to satisfy W >= 3 * tauHat.
            SetField(engine, "_tauHat", 0.5); // tauHat = 0.5 s => floor = 1.5 s
            double expectedFloor = Math.Max(GetField<double>(engine, "_wMin"), 3.0 * GetField<double>(engine, "_tauHat"));

            // Force the FSM into Hold mode and intentionally violate the tau floor with a short window.
            var fsmType = typeof(AdaptiveWindowEngine).GetNestedType("FSM", BindingFlags.NonPublic);
            var holdState = Enum.Parse(fsmType!, "Hold");
            SetField(engine, "_fsm", holdState);
            SetField(engine, "_W", 0.5); // below the 1.5 s tau floor

            // Trigger the Hold-mode maintenance path that should restore the tau-floor invariant.
            // Arguments below are placeholders; Hold-mode logic ignores the measurement content and enforces TauLowerBound.
            Invoke(engine, "AdaptState",
                0L,           // nowUs
                1.0,          // Y
                1.0,          // sigY
                1.0,          // m1
                1.0,          // varM1
                1,            // gatesUsed
                false,        // hasAnySignificance
                false,        // singlesChange
                true,         // correlationChange (keeps the FSM in Hold)
                false,        // degraded
                0.0);         // maxAbsZ

            double newW = GetField<double>(engine, "_W");
            double wMax = GetField<double>(engine, "_wMax");

            Assert.True(double.IsFinite(newW), "Window length must remain finite.");
            Assert.True(newW >= expectedFloor - 1e-9, $"Hold-mode must enforce tau floor; expected at least {expectedFloor}, got {newW}.");
            Assert.True(newW <= wMax, "Window length must not exceed configured maximum.");
        }

        private static T GetField<T>(object obj, string name)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var field = obj.GetType().GetField(name, flags);
            if (field == null)
                throw new ArgumentException($"Field '{name}' not found on type {obj.GetType()}");

            return (T)field.GetValue(obj)!;
        }

        private static void SetField<T>(object obj, string name, T value)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var field = obj.GetType().GetField(name, flags);
            if (field == null)
                throw new ArgumentException($"Field '{name}' not found on type {obj.GetType()}");

            field.SetValue(obj, value);
        }

        private static object Invoke(object obj, string methodName, params object[] args)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.InvokeMethod;
            return obj.GetType().InvokeMember(methodName, flags, binder: null, target: obj, args: args);
        }
    }
}
