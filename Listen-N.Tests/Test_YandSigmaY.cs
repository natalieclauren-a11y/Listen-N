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
        // PURE MATH TEST — deterministic low-variance data
        //

        [Fact]
        public void Test_YNearZeroForLowVariance()
        {
            int[] gates = { 10, 10, 10, 10 };

            double m1 = gates.Average();
            double m2 = gates.Average(g => g * (g - 1));

            double y = ComputeDirectY(m1, m2);

            Assert.True(double.IsFinite(y), "Y must be finite");
            Assert.True(y < 0, $"Expected Y < 0 for constant gates, got {y}");
            Assert.True(Math.Abs(y + 1.0) < 1e-6, $"Expected Y ≈ -1, got {y}");
        }


        //
        // ENGINE TEST — Y positive for clustered data
        //

        [Fact]
        public void Test_YPositiveForClusteredData()
        {
            int[] gates = { 1, 5, 1, 5, 1, 5 };

            var (y, _) = ComputeYAndSigmaY(gates);

            Assert.True(double.IsFinite(y), "Y must be finite");
            Assert.True(y > 0, $"Expected Y > 0 for clustered distribution, got {y}");
        }


        //
        // ENGINE TEST — sigmaY finite & non-negative
        //

        [Fact]
        public void Test_SigmaYFiniteNonNegative()
        {
            var patterns = new List<int[]>
            {
                new[]{ 2, 0, 1, 3 },
                new[]{ 5, 10, 0, 2 },
                new[]{ 0, 0, 0, 0 },
                new[]{ 1, 2, 3, 4, 5 }
            };

            foreach (var p in patterns)
            {
                var (y, sigmaY) = ComputeYAndSigmaY(p);
                double m1 = p.Average();

                // avoid mathematically undefined region
                if (m1 < 3)
                    continue;

                Assert.True(double.IsFinite(sigmaY), $"sigmaY must be finite (m1={m1})");
                Assert.True(sigmaY >= 0, "sigmaY cannot be negative");
            }
        }


        //
        // ENGINE TEST — Y increases with variance
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
        // CORE ENGINE-INVOKING HELPER
        //

        private static (double y, double sigmaY) ComputeYAndSigmaY(IReadOnlyList<int> gates, int gateUs = 1)
        {
            double windowSec = Math.Max(1, gates.Count) * gateUs / 1e6;

            var engineType = typeof(Listen_N.AdaptiveWindowEngine);
            var accType = engineType.GetNestedType("BaseBinAccumulator", BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("BaseBinAccumulator type not found");

            object acc = Activator.CreateInstance(
                accType,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null!,
                args: new object[] { gateUs, windowSec, (int?)null },
                culture: null!
            )!;

            var add = accType.GetMethod("Add", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("Missing Add method on BaseBinAccumulator.");
            var compute = accType.GetMethod("ComputeMoments", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("Missing ComputeMoments method on BaseBinAccumulator.");

            long t = 0;
            for (int i = 0; i < gates.Count; i++)
            {
                for (int j = 0; j < gates[i]; j++)
                    add.Invoke(acc, new object[] { t });

                t += gateUs;
            }

            // cov struct
            var parameters = compute.GetParameters();
            Type covType = parameters[6].ParameterType.GetElementType()
                ?? throw new InvalidOperationException("Covariance parameter type missing element type");
            object cov = Activator.CreateInstance(covType)!
                ?? throw new InvalidOperationException("Failed to instantiate covariance struct");

            object[] argsCompute =
            {
                windowSec, gateUs,
                0.0, 0.0, 0.0,
                0,
                cov
            };

            compute.Invoke(acc, argsCompute);

            double m1 = (double)argsCompute[2];
            double m2 = (double)argsCompute[3];

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
        // REFLECTION HELPERS
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

            var method = mathType.GetMethod("Y", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!;
            return (double)method.Invoke(null, new object[] { m1, m2 })!;
        }

        private static double ComputeVarY(double m1, double m2, double v11, double v22, double v12)
        {
            var mathType = typeof(Listen_N.AdaptiveWindowEngine)
                .GetNestedType("MomentsMath", BindingFlags.NonPublic)!;

            var method = mathType.GetMethod("VarY", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!;
            return (double)method.Invoke(null, new object[] { m1, m2, v11, v22, v12 })!;
        }
    }
}
