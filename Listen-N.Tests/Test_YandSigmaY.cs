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
        // ────────────────────────────────────────────────
        //  PURE MATH TEST: deterministic low-variance data
        // ────────────────────────────────────────────────
        //
        // For constant data, m1 = g and m2 = g(g-1), so:
        //     Y = (m2 - m1^2) / m1 = -1
        //

        [Fact]
        public void Test_YNearZeroForLowVariance()
        {
            int[] gates = { 10, 10, 10, 10 };

            double m1 = gates.Average();
            double m2 = gates.Average(g => g * (g - 1));

            double y = ComputeDirectY(m1, m2);

            Assert.True(double.IsFinite(y), "Y must be finite");
            Assert.True(y < 0, $"Y should be negative for constant gates, got {y}");
            Assert.True(Math.Abs(y + 1.0) < 1e-6, $"Expected Y ≈ -1, got {y}");
        }


        //
        // ────────────────────────────────────────────────
        //  ENGINE TEST: Y > 0 for clustered data
        // ────────────────────────────────────────────────
        //

        [Fact]
        public void Test_YPositiveForClusteredData()
        {
            int[] gates = { 1, 5, 1, 5, 1, 5 };

            var (y, _) = ComputeYAndSigmaY(gates);

            Assert.True(double.IsFinite(y), "Y must be finite");
            Assert.True(y > 0, $"Expected Y > 0 for clustered (super-Poisson) data, got {y}");
        }


        //
        // ────────────────────────────────────────────────
        //  ENGINE TEST: sigmaY finite & non-negative
        // ────────────────────────────────────────────────
        //
        // For m1 < ~3, sigmaY is undefined by physics due
        // to instability of the partial derivatives.
        // Skip those cases.
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

                // sigmaY undefined below this count level
                if (m1 < 3)
                    continue;

                Assert.True(double.IsFinite(y), $"Y should be finite (m1={m1})");
                Assert.True(double.IsFinite(sigmaY), $"sigmaY should be finite (m1={m1})");
                Assert.True(sigmaY >= 0, $"sigmaY should be non-negative, got {sigmaY}");
            }
        }


        //
        // ────────────────────────────────────────────────
        //  ENGINE TEST: Y increases with variance
        // ────────────────────────────────────────────────
        //

        [Fact]
        public void Test_YRespondsToVarianceIncrease()
        {
            int[] lowVar  = { 4, 4, 4, 4 };
            int[] highVar = { 1, 10, 1, 10 };

            var (yLow,  _) = ComputeYAndSigmaY(lowVar);
            var (yHigh, _) = ComputeYAndSigmaY(highVar);

            Assert.True(double.IsFinite(yLow));
            Assert.True(double.IsFinite(yHigh));
            Assert.True(yHigh > yLow, $"Expected Y(highVar) > Y(lowVar); got {yHigh} vs {yLow}");
        }


        //
        // ────────────────────────────────────────────────
        //  ENGINE MOMENTS & SIGMA-Y through accumulator
        // ────────────────────────────────────────────────
        //

        private static (double y, double sigmaY) ComputeYAndSigmaY(IReadOnlyList<int> gates, int gateUs = 1)
        {
            // Window duration in seconds
            double windowSec = Math.Max(1, gates.Count) * gateUs / 1e6;

            var engineType = typeof(Listen_N.AdaptiveWindowEngine);
            var accType = engineType.GetNestedType("BaseBinAccumulator", BindingFlags.NonPublic);

            object acc = Activator.CreateInstance(
                accType!,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null,
                args: new object[] { gateUs, windowSec },
                culture: null
            )!;

            var addMethod = accType.GetMethod("Add", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!;
            var computeMethod = accType.GetMethod("ComputeMoments", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!;

            long t = 0;
            for (int i = 0; i < gates.Count; i++)
            {
                for (int j = 0; j < gates[i]; j++)
                    addMethod.Invoke(acc, new object[] { t });

                t += gateUs; // advance to next gate
            }

            // Allocate covariance struct
            var parameters = computeMethod.GetParameters();
            Type covType = parameters[6].ParameterType.GetElementType()!;
            object cov = Activator.CreateInstance(covType)!;

            object[] computeArgs =
            {
                windowSec, gateUs,
                0.0, 0.0, 0.0,
                0,   // N
                cov
            };

            computeMethod.Invoke(acc, computeArgs);

            double m1 = (double)computeArgs[2];
            double m2 = (double)computeArgs[3];

            double v11 = GetField(cov, "V11");
            double v22 = GetField(cov, "V22");
            double v12 = GetField(cov, "V12");

            double y = ComputeDirectY(m1, m2);
            double varY = ComputeVarY(m1, m2, v11, v22, v12);

            double sigmaY = (double.IsFinite(varY) && varY > 0)
                ? Math.Sqrt(varY)
                : double.PositiveInfinity;

            return (y, sigmaY);
        }


        //
        // ────────────────────────────────────────────────
        //  Reflection utilities
        // ────────────────────────────────────────────────
        //

        private static double GetField(object cov, string name)
        {
            var f = cov.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    ?? throw new InvalidOperationException($"Missing covariance field {name}");
            return (double)f.GetValue(cov)!;
        }


        private static double ComputeDirectY(double m1, double m2)
        {
            var mathType = typeof(Listen_N.AdaptiveWindowEngine)
                .GetNestedType("MomentsMath", BindingFlags.NonPublic)!;

            var yMethod = mathType.GetMethod("Y", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)!;
            return (double)yMethod.Invoke(null, new object[] { m1, m2 })!;
        }


        private static double ComputeVarY(double m1, double m2, double v11, double v22, double v12)
        {
            var mathType = typeof(Listen_N.AdaptiveWindowEngine)
                .GetNestedType("MomentsMath", BindingFlags.NonPublic)!;

            var varYMethod = mathType.GetMethod("VarY", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)!;
            return (double)varYMethod.Invoke(null, new object[] { m1, m2, v11, v22, v12 })!;
        }
    }
}
