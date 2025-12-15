using System;
using System.Reflection;
using Listen_N;
using Xunit;

namespace AdaptiveWindowTests
{
    public class Test_PageHinkley
    {
        [Fact]
        public void Test_PageHinkley_GradualTrend_NoAlarm_NoHold()
        {
            using var engine = new AdaptiveWindowEngine(
                startWorker: false,
                enableFileLog: false);

            var fsmType = GetFsmType(engine);

            SetField(engine, "_fsm", Enum.Parse(fsmType, "Track"));
            SetField(engine, "_pendingFsm", Enum.Parse(fsmType, "Track"));
            SetField(engine, "_fsmConfirmations", 0);
            SetField(engine, "_phAlarm", false);
            SetField(engine, "_insufficientStatistics", false);
            SetField(engine, "_mismatchStreak", 0);
            SetField(engine, "_poissonQuietStreak", 0);
            SetField(engine, "_zPoisson", 0.0);

            double Y = 1.0;
            double sigY = 0.8;
            double m1 = 100.0;
            double varM1 = 100.0;
            long nowUs = 0;
            bool alarmTriggered = false;

            for (int t = 0; t < 1000; t++)
            {
                double x = 10.0 + 0.001 * t;
                bool alarm = (bool)Invoke(engine, "RateChange", x);
                alarmTriggered |= alarm;

                Invoke(engine, "AdaptState", nowUs, Y, sigY, m1, varM1, 10, true, alarm, false, false, 0.0);
                nowUs += 1_000_000;
            }

            bool phAlarm = GetField<bool>(engine, "_phAlarm");
            var fsm = GetField<object>(engine, "_fsm");

            Assert.False(alarmTriggered, "Gradual trend should not trigger Page-Hinkley alarm");
            Assert.False(phAlarm, "Page-Hinkley alarm should remain cleared");
            Assert.NotEqual(Enum.Parse(fsmType, "Hold"), fsm);
        }

        [Fact]
        public void Test_PageHinkley_SuddenJump_Alarm_TransitionsToHold()
        {
            using var engine = new AdaptiveWindowEngine(
                startWorker: false,
                enableFileLog: false);

            var fsmType = GetFsmType(engine);

            SetField(engine, "_fsm", Enum.Parse(fsmType, "Track"));
            SetField(engine, "_pendingFsm", Enum.Parse(fsmType, "Track"));
            SetField(engine, "_fsmConfirmations", 0);
            SetField(engine, "_phAlarm", false);
            SetField(engine, "_insufficientStatistics", false);
            SetField(engine, "_mismatchStreak", 0);
            SetField(engine, "_poissonQuietStreak", 0);
            SetField(engine, "_zPoisson", 0.0);

            double Y = 1.0;
            double sigY = 0.8;
            double m1 = 100.0;
            double varM1 = 100.0;
            long nowUs = 0;

            for (int i = 0; i < 50; i++)
            {
                Invoke(engine, "RateChange", 10.0);
                Invoke(engine, "AdaptState", nowUs, Y, sigY, m1, varM1, 10, true, false, false, false, 0.0);
                nowUs += 1_000_000;
            }

            bool jumpAlarm = (bool)Invoke(engine, "RateChange", 10_000.0);
            Assert.True(jumpAlarm, "Sudden jump should trigger Page-Hinkley alarm");

            Invoke(engine, "AdaptState", nowUs, Y, sigY, m1, varM1, 10, true, jumpAlarm, false, false, 0.0);
            nowUs += 1_000_000;
            Invoke(engine, "AdaptState", nowUs, Y, sigY, m1, varM1, 10, true, false, false, false, 0.0);

            var fsm = GetField<object>(engine, "_fsm");
            bool phAlarm = GetField<bool>(engine, "_phAlarm");

            Assert.Equal(Enum.Parse(fsmType, "Hold"), fsm);
            Assert.False(phAlarm, "Page-Hinkley alarm should clear upon entering Hold");
        }

        private static Type GetFsmType(object engine)
        {
            var fsmType = engine.GetType().GetNestedType("FSM", BindingFlags.NonPublic);
            if (fsmType == null)
                throw new InvalidOperationException("FSM type not found on AdaptiveWindowEngine");

            return fsmType;
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
