using System;
using System.Reflection;
using Listen_N;
using Xunit;

namespace AdaptiveWindowTests
{
    public class Test_AdaptWindow
    {
        [Fact]
        public void Test_AdaptWindow_RelYAboveEpsY_IncreasesW()
        {
            using var engine = new AdaptiveWindowEngine(
                windowStartSec: 1.0,
                windowMinSec: 0.5,
                windowMaxSec: 60.0,
                epsY: 0.10,
                epsM1: 0.02,
                startWorker: false,
                enableFileLog: false);

            SetField(engine, "_W", 1.0);

            double Y = 1.0;
            double sigY = 1.0;
            double m1 = 100.0;
            double varM1 = 100.0;

            double oldW = GetField<double>(engine, "_W");

            Invoke(engine, "AdaptWindow", Y, sigY, m1, varM1, false, 0.5, 2.0);

            double newW = GetField<double>(engine, "_W");
            double wMax = GetField<double>(engine, "_wMax");

            Assert.True(double.IsFinite(newW), "Window size should remain finite");
            Assert.True(newW > oldW, "Window size should increase when relY exceeds epsY");
            Assert.True(newW <= wMax, "Window size should not exceed wMax");
        }

        [Fact]
        public void Test_AdaptWindow_RelYAndRelM1BelowEps_DecreasesW()
        {
            using var engine = new AdaptiveWindowEngine(
                windowStartSec: 1.0,
                windowMinSec: 0.5,
                windowMaxSec: 60.0,
                epsY: 0.10,
                epsM1: 0.02,
                startWorker: false,
                enableFileLog: false);

            SetField(engine, "_W", 10.0);
            SetField(engine, "_tauHat", double.NaN);

            double Y = 10.0;
            double sigY = 0.1;
            double m1 = 1000.0;
            double varM1 = 0.01;

            double oldW = GetField<double>(engine, "_W");

            Invoke(engine, "AdaptWindow", Y, sigY, m1, varM1, true, 0.5, 2.0);

            double newW = GetField<double>(engine, "_W");
            double wMin = GetField<double>(engine, "_wMin");

            Assert.True(double.IsFinite(newW), "Window size should remain finite");
            Assert.True(newW < oldW, "Window size should decrease when relative uncertainties are below thresholds");
            Assert.True(newW >= wMin, "Window size should respect the minimum window bound");
        }

        [Fact]
        public void Test_AdaptWindow_TauLowerBoundRespected_WhenHonorTauFloorTrue()
        {
            using var engine = new AdaptiveWindowEngine(
                windowStartSec: 1.0,
                windowMinSec: 0.5,
                windowMaxSec: 60.0,
                epsY: 0.10,
                epsM1: 0.02,
                startWorker: false,
                enableFileLog: false);

            SetField(engine, "_W", 1.0);
            SetField(engine, "_tauHat", 2.0);

            double Y = 10.0;
            double sigY = 0.1;
            double m1 = 1000.0;
            double varM1 = 0.01;

            Invoke(engine, "AdaptWindow", Y, sigY, m1, varM1, true, 0.5, 2.0);

            double newW = GetField<double>(engine, "_W");
            double wMax = GetField<double>(engine, "_wMax");
            double expectedFloor = Math.Max(GetField<double>(engine, "_wMin"), 3.0 * GetField<double>(engine, "_tauHat"));

            Assert.True(double.IsFinite(newW), "Window size should remain finite");
            Assert.True(newW >= expectedFloor, "Window size should respect TauLowerBound when honorTauFloor is true");
            Assert.True(newW <= wMax, "Window size should not exceed wMax");
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
