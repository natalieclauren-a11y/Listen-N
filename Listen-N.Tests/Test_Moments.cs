using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Listen_N;
using Xunit;

namespace AdaptiveWindowTests
{
    public class Test_Moments
    {
        [Fact]
        public void Test_ZeroGates()
        {
            var gates = new[] { 0, 0, 0, 0 };

            var (m1, m2, m3, _) = ComputeMomentsFromGates(gates);

            Assert.InRange(m1, -1e-9, 1e-9);
            Assert.InRange(m2, -1e-9, 1e-9);
            Assert.InRange(m3, -1e-9, 1e-9);
        }

        [Fact]
        public void Test_Monotonicity()
        {
            var low = ComputeMomentsFromGates(new[] { 1, 1, 1, 1 });
            var mid = ComputeMomentsFromGates(new[] { 5, 5, 5, 5 });
            var high = ComputeMomentsFromGates(new[] { 10, 10, 10, 10 });

            Assert.True(mid.m1 > low.m1, "m1 should increase with gate counts");
            Assert.True(high.m1 > mid.m1, "m1 should increase with gate counts");

            Assert.True(mid.m2 > low.m2, "m2 should increase with gate counts");
            Assert.True(high.m2 > mid.m2, "m2 should increase with gate counts");

            Assert.True(mid.m3 > low.m3, "m3 should increase with gate counts");
            Assert.True(high.m3 > mid.m3, "m3 should increase with gate counts");
        }

        [Fact]
        public void Test_SamplePoisson()
        {
            const double lambda = 8.0;
            const int sampleSize = 200;
            var gates = GeneratePoissonSamples(sampleSize, lambda, seed: 424242);

            var (m1, m2, _, n) = ComputeMomentsFromGates(gates);

            Assert.Equal(sampleSize, n);

            double relativeError = Math.Abs(m1 - lambda) / lambda;
            Assert.True(relativeError < 0.2, $"m1 relative error too high: {relativeError}");

            Assert.True(m2 > 0, "m2 should be positive for Poisson data");

            double empiricalSecondFactorialMoment = gates.Average(g => (double)g * (g - 1));
            double scaleError = Math.Abs(m2 - empiricalSecondFactorialMoment) / Math.Max(empiricalSecondFactorialMoment, 1e-9);
            Assert.True(scaleError < 0.5, $"m2 deviates significantly from empirical n(n-1): {scaleError}");

            double m1Squared = m1 * m1;
            Assert.True(m2 > 0.2 * m1Squared && m2 < 5 * m1Squared, "m2 should scale with m1^2 within tolerance");
        }

        [Fact]
        public void Test_NoNaNsOrInfs()
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
                var (m1, m2, m3, n) = ComputeMomentsFromGates(pattern);

                Assert.True(double.IsFinite(m1), "m1 should be finite");
                Assert.True(double.IsFinite(m2), "m2 should be finite");
                Assert.True(double.IsFinite(m3), "m3 should be finite");

                Assert.True(m1 >= 0, "m1 should not be negative");
                Assert.True(m2 >= 0, "m2 should not be negative");
                Assert.True(m3 >= 0, "m3 should not be negative");

                Assert.Equal(pattern.Length, n);
            }
        }

        private static (double m1, double m2, double m3, int N)
    ComputeMomentsFromGates(IReadOnlyList<int> gates, int gateUs = 1)
        {
            if (gates == null)
                throw new ArgumentNullException(nameof(gates));

            double windowSec = Math.Max(1, gates.Count) * gateUs / 1e6;

            var accumulatorType = typeof(AdaptiveWindowEngine)
                .GetNestedType("BaseBinAccumulator", BindingFlags.NonPublic);

            if (accumulatorType == null)
                throw new InvalidOperationException("Unable to locate BaseBinAccumulator via reflection.");

            var accumulator = Activator.CreateInstance(
                accumulatorType,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null,
                args: new object[] { gateUs, windowSec },
                culture: null);

            var addMethod = accumulatorType.GetMethod("Add", BindingFlags.Instance | BindingFlags.Public);
            var computeMethod = accumulatorType.GetMethod("ComputeMoments", BindingFlags.Instance | BindingFlags.Public);

            // Feed synthetic gates
            for (int i = 0; i < gates.Count; i++)
            {
                long tUs = (long)(i * gateUs);
                for (int j = 0; j < gates[i]; j++)
                {
                    addMethod.Invoke(accumulator, new object[] { tUs });
                }
            }

            // Prepare real locals for out parameters
            double m1 = 0, m2 = 0, m3 = 0;
            int N = 0;

            // Get the underlying type of the by-ref covariance parameter
            var covParamType = computeMethod.GetParameters()[6].ParameterType;
            var covUnderlyingType = covParamType.IsByRef
                ? covParamType.GetElementType()
                : covParamType;

            // Instantiate the actual struct (NOT the by-ref type)
            var cov = Activator.CreateInstance(covUnderlyingType);

            // Prepare invocation args
            object[] invokeArgs =
            {
        windowSec,
        gateUs,
        m1,
        m2,
        m3,
        N,
        cov
    };

            // Call ComputeMoments via reflection
            computeMethod.Invoke(accumulator, invokeArgs);

            // Extract updated values
            double out_m1 = (double)invokeArgs[2];
            double out_m2 = (double)invokeArgs[3];
            double out_m3 = (double)invokeArgs[4];
            int out_N = (int)invokeArgs[5];

            return (out_m1, out_m2, out_m3, out_N);
        }


        private static IReadOnlyList<int> GeneratePoissonSamples(int count, double lambda, int seed)
        {
            var rng = new Random(seed);
            var samples = new List<int>(count);
            for (int i = 0; i < count; i++)
            {
                samples.Add(SamplePoisson(rng, lambda));
            }
            return samples;
        }

        private static int SamplePoisson(Random rng, double lambda)
        {
            double l = Math.Exp(-lambda);
            int k = 0;
            double p = 1.0;
            do
            {
                k++;
                p *= rng.NextDouble();
            }
            while (p > l);

            return k - 1;
        }
    }
}
