using System;
using System.Linq;
using Listen_N;
using Xunit;

namespace AdaptiveWindowTests
{
    public class Test_CorrelationTimeFit
    {
        [Fact]
        public void Test_ApproximateTauRecovery()
        {
            var tgUs = new[] { 100, 200, 400, 800, 1600 };
            var engine = new AdaptiveWindowEngine(gateLadderUs: tgUs, startWorker: false);

            const double a = 0.4;
            const double tauUs = 400.0;
            double tauSec = tauUs / 1e6;

            var rng = new Random(12345);
            var y = tgUs
                .Select(tg => a * (1.0 - Math.Exp(-(tg / 1e6) / tauSec)) * (1.0 + 0.02 * (rng.NextDouble() - 0.5)))
                .ToArray();

            var sigY = Enumerable.Repeat(0.01, tgUs.Length).ToArray();

            double tauHat = Fit(engine, y, sigY, out _);

            Assert.True(double.IsFinite(tauHat));
            double relativeError = Math.Abs(tauHat - tauSec) / tauSec;
            Assert.True(relativeError < 0.5, $"tau_hat should be within 50% of {tauSec}s; got {tauHat}");
        }

        [Fact]
        public void Test_NoisyTauStillReasonable()
        {
            var tgUs = new[] { 100, 200, 400, 800, 1600 };
            var engine = new AdaptiveWindowEngine(gateLadderUs: tgUs, startWorker: false);

            const double a = 0.4;
            const double tauUs = 400.0;
            double tauSec = tauUs / 1e6;

            var rng = new Random(4242);
            var y = tgUs
                .Select(tg =>
                {
                    double baseY = a * (1.0 - Math.Exp(-(tg / 1e6) / tauSec));
                    double noise = 0.15 * (rng.NextDouble() - 0.5); // ±7.5%
                    return baseY * (1.0 + noise);
                })
                .ToArray();

            var sigY = y.Select(v => Math.Max(0.05 * v, 1e-4)).ToArray();

            double tauHat = Fit(engine, y, sigY, out _);

            Assert.True(double.IsFinite(tauHat));
            Assert.True(tauHat > 0, "tau_hat should be positive even under noise");
            Assert.True(tauHat < tauSec * 10, $"tau_hat should not explode: {tauHat}");
        }

        [Fact]
        public void Test_FlatSequenceCausesMismatch()
        {
            var tgUs = new[] { 100, 200, 400, 800, 1600 };
            var engine = new AdaptiveWindowEngine(gateLadderUs: tgUs, startWorker: false);

            var y = Enumerable.Repeat(0.05, tgUs.Length).ToArray();
            var sigY = Enumerable.Repeat(0.01, y.Length).ToArray();

            double tauHat = Fit(engine, y, sigY, out var residuals);
            double rms = residuals.Length == 0 ? double.PositiveInfinity : Math.Sqrt(residuals.Select(r => r * r).Average());

            double epsY = 0.10; // match default EpsY used by AdaptiveWindowEngine
            bool mismatch = double.IsNaN(tauHat) || rms > (2.0 * epsY);
            Assert.True(mismatch, $"Expected mismatch: tau_hat={tauHat}, rms={rms}");
        }

        private static double Fit(AdaptiveWindowEngine engine, double[] y, double[] sigY, out double[] residuals)
        {
            var method = typeof(AdaptiveWindowEngine).GetMethod(
                "FitCorrelationTime",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

            if (method == null)
                throw new InvalidOperationException("Unable to find FitCorrelationTime via reflection.");

            object[] args = { y, sigY, null! };
            double tauHat = (double)method.Invoke(engine, args)!;
            residuals = (double[])args[2];
            return tauHat;
        }
    }
}
