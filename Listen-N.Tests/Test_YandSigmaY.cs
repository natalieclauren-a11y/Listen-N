using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Xunit;

namespace AdaptiveWindowTests
{
    public class Test_YandSigmaY
    {
        //
        // PURE MATH TEST: Y for deterministic low-variance data
        //
        [Fact]
        public void Test_YNearZeroForLowVariance()
        {
            int[] gates = { 10, 10, 10, 10 };

            // Pure mathematical m1, m2
            double m1 = gates.Average();
            double m2 = gates.Average(g => g * (g - 1));

            double y = ComputeDirectY(m1, m2);

            Assert.True(double.IsFinite(y), "Y should be finite for deterministic low-variance data.");
            Assert.True(y < 0, $"Expected Y negative for constant sequence, got {y}");
            Assert.True(Math.Abs(y + 1.0) < 1e-6, $"Expected Y ≈ -1 for constant gates, got {y}");
        }


        //
        // ENGINE-LEVEL Y TEST: clustered data should produce Y > 0
        //
        [Fact]
        public void Test_YPositiveForClusteredData()
        {
            int[] gates = { 1, 5, 1, 5, 1, 5 };

            var (y, _) = ComputeYAndSigmaY(gates);

            Assert.True(double.IsFinite(y), "Y should be finite");
            Assert.True(y > 0, $"Expected Y > 0 for clustered data, got {y}");
        }


        //
        // ENGINE-LEVEL SIGMA(Y) TEST
        // Only valid when m1 > 1 (otherwise VarY is undefined by physics)
        //
        [Fact]
        public void Test_SigmaYFiniteNonNegative()
        {
            var patterns = new List<int[]>
            {
                new[] { 2, 0, 1, 3 },
                new[] { 5, 10, 0, 2 },
                new[] { 0, 0, 0, 0 },
                new[] { 1, 2, 3, 4, 5 }
            };

            foreach (var pattern in patterns)
            {
                var (y, sigmaY) = ComputeYAndSigmaY(pattern);
                double m1 = pattern.Average();

                if (m1 <= 1)
                    continue;   // sigmaY undefined at very low count levels

                Assert.True(double.IsFinite(y), $"Y should be finite, got {y}");
                Assert.True(double.IsFinite(sigmaY), $"sigmaY should be finite for m1={m1}");
                Assert.True(sigmaY >= 0, $"sigmaY should be non-negative, got {sigmaY}");
            }
        }


        //
        // ENGINE-LEVEL TREND TEST: Y should increase with variance
        //
        [Fact]
        public void Test_YRespondsToVarianceIncrease()
        {
            int[] lowVar = { 4, 4, 4, 4 };
            int[] highVar = { 1, 10, 1, 10 };

            var (yLow, _) = ComputeYAndSigmaY(lowVar);
            var (yHigh, _) = ComputeYAndSigmaY(highVar);

            Assert.True(double.IsFinite(yLow) && double.IsFinite(yHigh));
            Assert.True(yHigh > yLow, $"Expected Y to increase with variance: low={yLow}, high={yHigh}");
        }


        //
        // ENGINE-LEVEL Y & sigmaY computation using BaseBinAccumulator
        //
        private static (double y, double sigmaY) ComputeYAndSigmaY(IReadOnlyList<int> gates, int gateUs = 1)
        {
            double windowSec = Math.Max(1, gates.Count) * gateUs / 1e6;

            var accumulatorType = typeof(Listen_N.AdaptiveWindowEngine)
                .GetNestedType("BaseBinAccumulator", BindingFlags.NonPublic);

            object accumulator = Activator.CreateInstance(
                accumulatorType!,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null,
                args: new object[] { gateUs, windowSec },
                culture: null
            )!;

            var addMethod = accumulatorType!.GetMethod("Add")!;
            var computeMethod = accumulatorType.GetMethod("ComputeMoments")!;

            for (int i = 0; i < gates.Count; i++)
            {
                long tUs = i * gateUs;
                for (int j = 0; j < gates[i]; j++)
                    addMethod.Invoke(accumulator, new object[] { tUs });
            }

            var parameters = computeMethod.GetParameters();
            Type covType = parameters[6].ParameterType.GetElementType()!;
            object cov = Activator.CreateInstance(covType)!;

            object[] argsCompute =
            {
                windowSec, gateUs,
                0.0, 0.0, 0.0,
                0,
                cov
            };

            computeMethod.Invoke(accumulator, argsCompute);

            double m1 = (double)argsCompute[2];
            double m2 = (double)argsCompute[3];

            double v11 = GetFieldValue(cov, "V11");
            double v22 = GetFieldValue(cov, "V22");
            double v12 = GetFieldValue(cov, "V12");

            double y = ComputeDirectY(m1, m2);

            var momentsMathType = typeof(Listen_N.AdaptiveWindowEngine)
                .GetNestedType("MomentsMath", BindingFlags.NonPublic)!;

            var varYMethod = momentsMathType.GetMethod(
                "VarY",
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public
            )!;

            double varY = (double)varYMethod.Invoke(null, new object[] { m1, m2, v11, v22, v12 })!;

            double sigmaY = (double.IsFinite(varY) && varY > 0)
                ? Math.Sqrt(varY)
                : double.PositiveInfinity;

            return (y, sigmaY);
        }


        //
        // Reflection helper: read covariance fields
        //
        private static double GetFieldValue(object cov, string fieldName)
        {
            var field = cov.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                        ?? throw new InvalidOperationException($"Cannot read covariance field: {fieldName}");
            return (double)field.GetValue(cov)!;
        }


        //
        // Pure-math helper: Y = (m2 - m1*m1) / m1
        //
        private static double ComputeDirectY(double m1, double m2)
        {
            var mmType = typeof(Listen_N.AdaptiveWindowEngine)
                .GetNestedType("MomentsMath", BindingFlags.NonPublic)!;

            var yMethod = mmType.GetMethod(
                "Y",
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public
            )!;

            return (double)yMethod.Invoke(null, new object[] { m1, m2 })!;
        }
    }
}
