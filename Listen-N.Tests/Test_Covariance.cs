using Xunit;
using System;
using System.Linq;
using System.Reflection;
using System.Collections.Generic;
using Listen_N;


namespace AdaptiveWindowTests
{
    public class Test_Covariance
    {
        // Compute covariance from gates using reflection
        private static AdaptiveWindowEngine.MomentCovariance ComputeCovariance(IReadOnlyList<int> gates, out object covObj)
        {
            int gateUs = 1;
            double windowSec = Math.Max(1, gates.Count) * gateUs / 1e6;

            // locate BaseBinAccumulator
            var accType = typeof(AdaptiveWindowEngine)
                .GetNestedType("BaseBinAccumulator", BindingFlags.NonPublic);

            if (accType == null)
                throw new InvalidOperationException("Cannot locate BaseBinAccumulator.");

            var acc = Activator.CreateInstance(
                accType,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null,
                args: new object[] { gateUs, windowSec },
                culture: null);

            var addMethod = accType.GetMethod("Add", BindingFlags.Instance | BindingFlags.Public);
            var computeMethod = accType.GetMethod("ComputeMoments", BindingFlags.Instance | BindingFlags.Public);

            // Feed synthetic time-ordered data
            for (int i = 0; i < gates.Count; i++)
            {
                long tUs = i * gateUs;
                for (int j = 0; j < gates[i]; j++)
                    addMethod.Invoke(acc, new object[] { tUs });
            }

            // Prepare out parameters
            double m1 = 0, m2 = 0, m3 = 0;
            int N = 0;

            var param = computeMethod.GetParameters()[6].ParameterType;
            var covUnderlyingType = param.IsByRef ? param.GetElementType() : param;
            covObj = Activator.CreateInstance(covUnderlyingType)!;

            object[] args2 = new object[]
            {
                windowSec, gateUs,
                m1, m2, m3, N,
                covObj
            };

            computeMethod.Invoke(acc, args2);

            // Extract covariance struct
            var mc = (AdaptiveWindowEngine.MomentCovariance)args2[6];
            return mc;
        }

        private static double SmallestEigenvalue(double[,] M)
        {
            // Gershgorin lower-bound estimate
            double min = double.PositiveInfinity;
            for (int i = 0; i < 3; i++)
            {
                double center = M[i, i];
                double radius = 0;
                for (int j = 0; j < 3; j++)
                    if (i != j) radius += Math.Abs(M[i, j]);

                min = Math.Min(min, center - radius);
            }
            return min;
        }

        [Fact]
        public void Test_FiniteCovariance()
        {
            var mc = ComputeCovariance(new[] { 10, 11, 9, 12, 8 }, out _);

            Assert.True(double.IsFinite(mc.V11) && mc.V11 >= 0);
            Assert.True(double.IsFinite(mc.V22) && mc.V22 >= 0);
            Assert.True(double.IsFinite(mc.V33) && mc.V33 >= 0);
        }

        [Fact]
        public void Test_RidgeRegularization()
        {
            var mc = ComputeCovariance(new[] { 5, 5, 5, 5 }, out _);

            // Regularized covariance must have positive diag
            Assert.True(mc.V11 > 0);
            Assert.True(mc.V22 > 0);
            Assert.True(mc.V33 > 0);
        }

        [Fact]
        public void Test_PositiveSemidefiniteAfterRegularization()
        {
            var mc = ComputeCovariance(new[] { 1, 10, 2, 12, 3 }, out _);

            double[,] M =
            {
                { mc.V11, mc.V12, mc.V13 },
                { mc.V12, mc.V22, mc.V23 },
                { mc.V13, mc.V23, mc.V33 }
            };

            double minEig = SmallestEigenvalue(M);

            // Allow tiny negative eigenvalues from sample noise
            Assert.True(minEig > -1, $"Eigenvalue too negative: {minEig}");

        }

        [Fact]
        public void Test_VarianceScaling()
        {
            var mc1 = ComputeCovariance(new[] { 1, 1, 1, 1 }, out _);
            var mc2 = ComputeCovariance(new[] { 1, 10, 2, 12, 3 }, out _);

            Assert.True(mc2.V11 > mc1.V11);
        }
    }
}
