using System;
using System.Collections.Generic;
using System.Reflection;
using Listen_N;
using Xunit;
using MathNet.Numerics.LinearAlgebra;

namespace AdaptiveWindowTests
{
    public class Test_Covariance
    {
        [Fact]
        public void Test_FiniteCovariance()
        {
            var gates = new[] { 10, 11, 9, 12, 8 };

            var cov = ComputeCovariance(gates, out _);

            Assert.True(double.IsFinite(cov.v11) && cov.v11 >= 0, "V11 should be finite and non-negative");
            Assert.True(double.IsFinite(cov.v22) && cov.v22 >= 0, "V22 should be finite and non-negative");
            Assert.True(double.IsFinite(cov.v33) && cov.v33 >= 0, "V33 should be finite and non-negative");
        }

        [Fact]
        public void Test_RidgeRegularization()
        {
            var gates = new[] { 5, 5, 5, 5 };

            ComputeCovariance(gates, out var covObject);
            var regCov = Regularize(covObject, out _);

            Assert.True(regCov.v11 > 0, "Regularization should make V11 strictly positive");
            Assert.True(regCov.v22 > 0, "Regularization should make V22 strictly positive");
            Assert.True(regCov.v33 > 0, "Regularization should make V33 strictly positive");
        }

        [Fact]
        public void Test_PositiveSemidefiniteAfterRegularization()
        {
            var gates = new[] { 3, 5, 4, 6, 7, 2, 4 };
            ComputeCovariance(gates, out var cov);

            // Use existing reflection wrapper
            Regularize(cov, out var reg);

            double[,] M = {
        { GetFieldValue(reg, "V11"), GetFieldValue(reg, "V12"), GetFieldValue(reg, "V13") },
        { GetFieldValue(reg, "V12"), GetFieldValue(reg, "V22"), GetFieldValue(reg, "V23") },
        { GetFieldValue(reg, "V13"), GetFieldValue(reg, "V23"), GetFieldValue(reg, "V33") }
    };

            var mat = Matrix<double>.Build.DenseOfArray(M);
            var ev = mat.Evd(Symmetricity.Symmetric);

            double lambdaMin = ev.EigenValues.Real().Minimum();

            Assert.True(lambdaMin >= -1e-6,
                $"Covariance matrix has negative eigenvalue λ_min = {lambdaMin}");
        }


        [Fact]
        public void Test_VarianceScaling()
        {
            var gates1 = new[] { 1, 1, 1, 1, 1 };
            var gates2 = new[] { 1, 10, 2, 12, 3 };

            var cov1 = ComputeCovariance(gates1, out _);
            var cov2 = ComputeCovariance(gates2, out _);

            Assert.True(cov2.v11 > cov1.v11, "More variable gates should produce a larger variance in m1");
        }

        private static (double v11, double v22, double v33) ComputeCovariance(IReadOnlyList<int> gates, out object covObject)
        {
            if (gates == null) throw new ArgumentNullException(nameof(gates));

            int gateUs = 1;
            double windowSec = Math.Max(1, gates.Count) * gateUs / 1e6;

            var accumulatorType = typeof(AdaptiveWindowEngine).GetNestedType("BaseBinAccumulator", BindingFlags.NonPublic);
            if (accumulatorType == null)
                throw new InvalidOperationException("Unable to locate BaseBinAccumulator via reflection.");

            object? accumulator = Activator.CreateInstance(
                accumulatorType,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null,
                args: new object[] { gateUs, windowSec, (int?)null },
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
            Type rawType = parameters[6].ParameterType;

            // If it's a by-ref type (MomentCovariance&), get the underlying struct type
            Type actualType = rawType.IsByRef ? rawType.GetElementType()! : rawType;

            covObject = Activator.CreateInstance(actualType)!;

            object[] invokeArgs = new object[]
            {
               windowSec,
               gateUs,
               0.0,
               0.0,
               0.0,
               0,
               covObject
                       };

            computeMethod.Invoke(accumulator, invokeArgs);

            // Get the updated covariance struct back out
            covObject = invokeArgs[6];

            double v11 = GetFieldValue(covObject, "V11");
            double v22 = GetFieldValue(covObject, "V22");
            double v33 = GetFieldValue(covObject, "V33");
            return (v11, v22, v33);
        }

        private static (double v11, double v22, double v33) Regularize(object rawCov, out object regCov)
        {
            using var engine = new AdaptiveWindowEngine(startWorker: false);
            var regMethod = typeof(AdaptiveWindowEngine).GetMethod("RegularizeCov", BindingFlags.Instance | BindingFlags.NonPublic);
            if (regMethod == null)
                throw new InvalidOperationException("Unable to locate RegularizeCov via reflection.");

            regCov = Activator.CreateInstance(rawCov.GetType())!;
            object[] args = new[] { rawCov, regCov };
            regMethod.Invoke(engine, args);
            regCov = args[1];

            double v11 = GetFieldValue(regCov, "V11");
            double v22 = GetFieldValue(regCov, "V22");
            double v33 = GetFieldValue(regCov, "V33");
            return (v11, v22, v33);
        }

        private static double GetFieldValue(object cov, string fieldName)
        {
            var field = cov.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (field == null)
                throw new InvalidOperationException($"Unable to read covariance field {fieldName}.");
            return (double)field.GetValue(cov)!;
        }

        private static double GershgorinLowerBound(object cov)
        {
            double v11 = GetFieldValue(cov, "V11");
            double v22 = GetFieldValue(cov, "V22");
            double v33 = GetFieldValue(cov, "V33");
            double v12 = GetFieldValue(cov, "V12");
            double v13 = GetFieldValue(cov, "V13");
            double v23 = GetFieldValue(cov, "V23");

            double row1 = v11 - (Math.Abs(v12) + Math.Abs(v13));
            double row2 = v22 - (Math.Abs(v12) + Math.Abs(v23));
            double row3 = v33 - (Math.Abs(v13) + Math.Abs(v23));

            return Math.Min(row1, Math.Min(row2, row3));
        }
    }
}
