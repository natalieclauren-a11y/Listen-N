using System;
using System.Linq;
using System.Reflection;
using Listen_N;
using Xunit;

namespace AdaptiveWindowTests
{
    public class Test_GateSelection
    {
        [Fact]
        public void Test_GateSelection_ClearPlateau_SelectsCorrectIndex()
        {
            // Gate ladder expanded to match the injected Y_k array length.
            var gateLadder = new[] { 500, 1000, 2000, 4000, 8000, 16000, 32000, 64000 };
            using var engine = new AdaptiveWindowEngine(
                gateLadderUs: gateLadder,
                startWorker: false,
                enableFileLog: false);

            // Ensure previous gate history does not influence selection.
            ResetGateState(engine, 0);

            // Synthetic Feynman-Y sequence with a clear plateau beginning near index 5.
            var yk = new[] { 0.10, 0.25, 0.42, 0.48, 0.50, 0.501, 0.499, 0.502 };
            int expectedPlateauIndex = 5;

            int tgIndex = ApplySyntheticGateSelection(engine, yk);

            Assert.InRange(tgIndex, 0, yk.Length - 1);
            Assert.Equal(expectedPlateauIndex, tgIndex);

            // Deterministic behavior: repeating the same Y_k leaves TgIndex unchanged.
            int repeatIndex = ApplySyntheticGateSelection(engine, yk);
            Assert.Equal(tgIndex, repeatIndex);
        }

        [Fact]
        public void Test_GateSelection_NoisyPlateau_TgDoesNotFlap()
        {
            using var engine = new AdaptiveWindowEngine(startWorker: false, enableFileLog: false);

            ResetGateState(engine, 1);

            // Base plateau; noise added well within the eta tolerance so the plateau index should stay stable.
            var baseY = new[] { 0.12, 0.28, 0.45, 0.49, 0.50, 0.50, 0.50 };
            var rng = new Random(42);

            int iterations = 12;
            int[] indices = new int[iterations];
            for (int i = 0; i < iterations; i++)
            {
                double[] noisy = baseY
                    .Select(y => y + (rng.NextDouble() - 0.5) * 0.002)
                    .ToArray();

                indices[i] = ApplySyntheticGateSelection(engine, noisy);
            }

            int minIdx = indices.Min();
            int maxIdx = indices.Max();

            // TgIndex should not oscillate wildly; allow at most +/-1 drift.
            Assert.True(maxIdx - minIdx <= 1, $"TgIndex varied too much: range [{minIdx}, {maxIdx}]");

            // Once settled, the selection should remain stable for the tail iterations.
            int finalIdx = indices[^1];
            Assert.All(indices.Skip(indices.Length / 2), idx => Assert.Equal(finalIdx, idx));
        }

        private static int ApplySyntheticGateSelection(AdaptiveWindowEngine engine, double[] yk)
        {
            int desiredIdx = ComputeDesiredGateIndex(engine, yk);

            // The private UpdateGateSelection method requires repeated confirmations; call twice to mirror the internal logic.
            Invoke(engine, "UpdateGateSelection", desiredIdx);
            Invoke(engine, "UpdateGateSelection", desiredIdx);

            return GetField<int>(engine, "_tgIdx");
        }

        private static int ComputeDesiredGateIndex(AdaptiveWindowEngine engine, double[] yk)
        {
            var tgUs = GetField<int[]>(engine, "_tgUs");
            double etaFrac = GetField<double>(engine, "_etaFrac");

            if (yk.Length != tgUs.Length)
                throw new ArgumentException("Length of Y_k array must match gate ladder length.");

            int significantIdx = -1;
            int plateauIdx = -1;

            for (int k = 0; k < yk.Length; k++)
            {
                double y = yk[k];
                bool hasSig = y > 0; // Synthetic sequences are positive; treat them as significant gates.
                if (hasSig && significantIdx < 0)
                {
                    significantIdx = k;
                }

                if (hasSig && k > significantIdx)
                {
                    double dlog = Math.Log((double)tgUs[k] / tgUs[k - 1]);
                    if (dlog > 0)
                    {
                        double slope = Math.Abs((yk[k] - yk[k - 1]) / dlog);
                        double thresh = etaFrac * Math.Abs(yk[k]);
                        if (slope < thresh)
                        {
                            plateauIdx = k;
                        }
                    }
                }
            }

            if (significantIdx < 0)
            {
                return 0;
            }

            return (plateauIdx >= significantIdx && plateauIdx >= 0) ? plateauIdx : significantIdx;
        }

        private static void ResetGateState(AdaptiveWindowEngine engine, int tgIndex)
        {
            SetField(engine, "_tgIdx", tgIndex);
            SetField(engine, "_pendingTgIdx", -1);
            SetField(engine, "_tgConfirmations", 0);
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
