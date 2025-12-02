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
        public void Test_ConstantCounts()
        {
            var gates = new[] { 10, 10, 10 };
            var (m1, m2, m3, n) = ComputeMomentsFromGates(gates);

            Assert.Equal(10.0, m1);
            Assert.Equal(90.0, m2);
            Assert.Equal(720.0, m3);
            Assert.Equal(3, n);
        }

        [Fact]
        public void Test_MixedCounts()
        {
            var gates = new[] { 0, 5, 10 };
            var (m1, m2, m3, n) = ComputeMomentsFromGates(gates);

            Assert.Equal(5.0, m1);
            Assert.Equal(110.0 / 3.0, m2, 10);
            Assert.Equal(780.0 / 3.0, m3, 10);
            Assert.Equal(3, n);
        }

        [Fact]
        public void Test_EdgeCaseZeroGate()
        {
            var gates = new[] { 0, 0, 0 };
            var (m1, m2, m3, n) = ComputeMomentsFromGates(gates);

            Assert.Equal(0.0, m1);
            Assert.Equal(0.0, m2);
            Assert.Equal(0.0, m3);
            Assert.Equal(3, n);
        }

        [Fact]
        public void Test_PoissonSanity()
        {
            const double lambda = 8.0;
            const int sampleSize = 20000;
            var gates = GeneratePoissonSamples(sampleSize, lambda, seed: 12345);
            var (m1, m2, m3, n) = ComputeMomentsFromGates(gates);

            Assert.Equal(sampleSize, n);
            Assert.InRange(m1, lambda - 0.1, lambda + 0.1);
            Assert.InRange(m2, lambda * lambda - 0.8, lambda * lambda + 0.8);
            Assert.InRange(m3, Math.Pow(lambda, 3) - 5.0, Math.Pow(lambda, 3) + 5.0);
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
            // Knuth algorithm for small to moderate lambda
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
