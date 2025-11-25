using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Threading;

namespace Listen_N
{
    /// <summary>
    /// Provides deterministic synthetic timestamp streams for exercising <see cref="AdaptiveWindowEngine"/>.
    /// </summary>
    public static class SyntheticTimestampGenerator
    {
        public static IEnumerable<long> StableHighRate(int seed, double durationSec = 8.0)
            => Generate(durationSec, _ => 5_000.0, seed);

        public static IEnumerable<long> StableLowRate(int seed, double durationSec = 12.0)
            => Generate(durationSec, _ => 25.0, seed);

        public static IEnumerable<long> SlowlyDriftingRate(int seed, double durationSec = 12.0)
            => Generate(durationSec, t => 800.0 + 400.0 * (t / durationSec), seed);

        public static IEnumerable<long> SuddenJumpRate(int seed, double durationSec = 12.0, double jumpTimeSec = 6.0)
            => Generate(durationSec, t => t < jumpTimeSec ? 300.0 : 3_000.0, seed);

        private static IEnumerable<long> Generate(double durationSec, Func<double, double> rateHz, int seed)
        {
            var rng = new Random(seed);
            double tUs = 0.0;
            double endUs = durationSec * 1e6;
            while (tUs < endUs)
            {
                double rate = Math.Max(1e-6, rateHz(tUs / 1e6));
                double meanIntervalUs = 1e6 / rate;
                double u = Math.Clamp(rng.NextDouble(), double.Epsilon, 1.0 - double.Epsilon);
                double dtUs = -Math.Log(u) * meanIntervalUs;
                tUs += dtUs;
                yield return (long)tUs;
            }
        }
    }

    /// <summary>
    /// Runs synthetic streams through the adaptive engine and logs step-by-step state to CSV.
    /// </summary>
    public static class AdaptiveWindowEngineHarness
    {
        private record Scenario(string Name, IEnumerable<long> Timestamps);

        public static void RunAll(string csvPath)
        {
            var scenarios = new[]
            {
                new Scenario("stable_high_rate", SyntheticTimestampGenerator.StableHighRate(seed: 1337)),
                new Scenario("stable_low_rate", SyntheticTimestampGenerator.StableLowRate(seed: 2024)),
                new Scenario("slowly_drifting_rate", SyntheticTimestampGenerator.SlowlyDriftingRate(seed: 4242)),
                new Scenario("sudden_jump_rate", SyntheticTimestampGenerator.SuddenJumpRate(seed: 9001))
            };

            using var writer = new StreamWriter(csvPath);
            writer.WriteLine(string.Join(",", new[]
            {
                "scenario",
                "estimate_index",
                "now_us",
                "fsm_state",
                "tg_us",
                "W_sec",
                "S_sec",
                "tau_hat_sec",
                "m1_hat",
                "m2_hat",
                "m3_hat",
                "Y_hat",
                "sigma_Y",
                "has_significance",
                "is_low_rate",
                "is_degraded",
                "is_stats_bound",
                "insufficient_statistics",
                "model_mismatch"
            }));

            foreach (var scenario in scenarios)
            {
                RunScenario(scenario, writer);
            }
        }

        private static void RunScenario(Scenario scenario, StreamWriter writer)
        {
            using var engine = new AdaptiveWindowEngine();
            var tauField = typeof(AdaptiveWindowEngine).GetField("_tauHat", BindingFlags.Instance | BindingFlags.NonPublic);
            var stepField = typeof(AdaptiveWindowEngine).GetField("_S", BindingFlags.Instance | BindingFlags.NonPublic);

            int estimateIdx = 0;
            var sw = Stopwatch.StartNew();
            var quietTimer = Stopwatch.StartNew();
            object sync = new();

            engine.OnEstimate += est =>
            {
                double tau = tauField?.GetValue(engine) is double t ? t : double.NaN;
                double step = stepField?.GetValue(engine) is double s ? s : double.NaN;

                lock (sync)
                {
                    writer.WriteLine(string.Join(",", new[]
                    {
                        scenario.Name,
                        estimateIdx.ToString(CultureInfo.InvariantCulture),
                        est.NowUs.ToString(CultureInfo.InvariantCulture),
                        est.State,
                        est.GateUs.ToString(CultureInfo.InvariantCulture),
                        est.WindowSec.ToString(CultureInfo.InvariantCulture),
                        step.ToString(CultureInfo.InvariantCulture),
                        tau.ToString(CultureInfo.InvariantCulture),
                        est.M1.ToString(CultureInfo.InvariantCulture),
                        est.M2.ToString(CultureInfo.InvariantCulture),
                        est.M3.ToString(CultureInfo.InvariantCulture),
                        est.Y.ToString(CultureInfo.InvariantCulture),
                        est.SigmaY.ToString(CultureInfo.InvariantCulture),
                        est.HasSignificance.ToString(CultureInfo.InvariantCulture),
                        est.IsLowRate.ToString(CultureInfo.InvariantCulture),
                        est.IsDegraded.ToString(CultureInfo.InvariantCulture),
                        est.IsStatsBound.ToString(CultureInfo.InvariantCulture),
                        est.InsufficientStatistics.ToString(CultureInfo.InvariantCulture),
                        est.ModelMismatch.ToString(CultureInfo.InvariantCulture)
                    }));
                    estimateIdx++;
                    quietTimer.Restart();
                }
            };

            foreach (long t in scenario.Timestamps)
            {
                engine.OnDetection(new Detection(t));
            }

            // allow the worker to finish emitting estimates
            while (quietTimer.Elapsed < TimeSpan.FromMilliseconds(500) && sw.Elapsed < TimeSpan.FromSeconds(10))
            {
                Thread.Sleep(50);
            }

            writer.Flush();
        }
    }
}
