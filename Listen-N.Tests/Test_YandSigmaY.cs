using System;
using System.Collections.Generic;
using System.Reflection;
using Xunit;

namespace AdaptiveWindowTests
{
    public class Test_YandSigmaY
    {
        [Fact]
        public void Test_YNearZeroForLowVariance()
        {
            var gates = new[] { 10, 10, 10, 10 };

            var (y, _) = ComputeYAndSigmaY(gates);

            Assert.True(Math.Abs(y) < 0.2, $"Expected Y to be near zero for low-variance data, got {y}");
        }

        [Fact]
        public void Test_YPositiveForClusteredData()
        {
            var gates = new[] { 1, 5, 1, 5, 1, 5 };

            var (y, _) = ComputeYAndSigmaY(gates);

            Assert.True(y > 0, $"Expected Y to be positive for clustered data, got {y}");
        }

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
                var (_, sigmaY) = ComputeYAndSigmaY(pattern);

                Assert.True(double.IsFinite(sigmaY), "sigmaY should be finite");
                Assert.True(sigmaY >= 0, "sigmaY should be non-negative");
                Assert.False(double.IsNaN(sigmaY) || double.IsInfinity(sigmaY), "sigmaY should not be NaN or Infinity");
            }
        }

        [Fact]
        public void Test_YRespondsToVarianceIncrease()
        {
            var lowVar = new[] { 4, 4, 4, 4 };
            var highVar = new[] { 1, 10, 1, 10 };

            var (yLow, _) = ComputeYAndSigmaY(lowVar);
            var (yHigh, _) = ComputeYAndSigmaY(highVar);

            Assert.True(yHigh > yLow, $"Expected Y to increase with variance: low={yLow}, high={yHigh}");
        }

        private static (double y, double sigmaY) ComputeYAndSigmaY(IReadOnlyList<int> gates, int gateUs = 1)
        {
            if (gates == null) throw new ArgumentNullException(nameof(gates));

            double windowSec = Math.Max(1, gates.Count) * gateUs / 1e6;

            var accumulatorType = typeof(Listen_N.AdaptiveWindowEngine).GetNestedType("BaseBinAccumulator", BindingFlags.NonPublic);
            if (accumulatorType == null)
                throw new InvalidOperationException("Unable to locate BaseBinAccumulator via reflection.");

            object? accumulator = Activator.CreateInstance(
                accumulatorType,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null,
                args: new object[] { gateUs, windowSec },
                culture: null);
            if (accumulator == null)
                throw new InvalidOperationException("Failed to instantiate BaseBinAccumulator.");

            var addMethod = accumulatorType.GetMethod("Add", BindingFlags.Instance | BindingFlags.Public);
            var computeMethod = accumulatorType.GetMethod("ComputeMoments", BindingFlags.Instance | BindingFlags.Public);
            if (addMethod == null || computeMethod == null)
                throw new InvalidOperationException("Missing expected BaseBinAccumulator methods.");

            for (int i = 0; i < gates.Count; i++)
            {
                long tUs = (long)(i * gateUs);
                for (int j = 0; j < gates[i]; j++)
                {
                    addMethod.Invoke(accumulator, new object[] { tUs });
                }
            }

            var parameters = computeMethod.GetParameters();
            Type rawCovType = parameters[6].ParameterType;
            Type covValueType = rawCovType.IsByRef ? rawCovType.GetElementType()! : rawCovType;

            object cov = Activator.CreateInstance(covValueType)!;
            object[] invokeArgs = new object[] { windowSec, gateUs, 0.0, 0.0, 0.0, 0, cov };

            computeMethod.Invoke(accumulator, invokeArgs);

            double m1 = (double)invokeArgs[2];
            double m2 = (double)invokeArgs[3];
            cov = invokeArgs[6];

            double v11 = GetFieldValue(cov, "V11");
            double v22 = GetFieldValue(cov, "V22");
            double v12 = GetFieldValue(cov, "V12");

            var momentsMathType = typeof(Listen_N.AdaptiveWindowEngine).GetNestedType("MomentsMath", BindingFlags.NonPublic);
            if (momentsMathType == null)
                throw new InvalidOperationException("Unable to locate MomentsMath via reflection.");

            var yMethod = momentsMathType.GetMethod("Y", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            var varYMethod = momentsMathType.GetMethod("VarY", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            if (yMethod == null || varYMethod == null)
                throw new InvalidOperationException("Unable to locate MomentsMath methods.");

            double y = (double)yMethod.Invoke(null, new object[] { m1, m2 })!;
            double varY = (double)varYMethod.Invoke(null, new object[] { m1, m2, v11, v22, v12 })!;

            double sigmaY = double.IsFinite(varY) && varY > 0 ? Math.Sqrt(varY) : double.PositiveInfinity;

            return (y, sigmaY);
        }

        private static double GetFieldValue(object cov, string fieldName)
        {
            var field = cov.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (field == null)
                throw new InvalidOperationException($"Unable to read covariance field {fieldName}.");
            return (double)field.GetValue(cov)!;
        }
    }
}
