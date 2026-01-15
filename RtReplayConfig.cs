using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Listen_N
{
    public sealed class RtReplayConfig
    {
        [JsonPropertyName("version")]
        public string Version { get; init; } = string.Empty;

        [JsonPropertyName("git_commit")]
        public string? GitCommit { get; init; }

        [JsonPropertyName("step_period_s")]
        public double StepPeriodS { get; init; }

        [JsonPropertyName("bin_width_s")]
        public double BinWidthS { get; init; }

        [JsonPropertyName("window_start_s")]
        public double WindowStartS { get; init; }

        [JsonPropertyName("window_min_s")]
        public double WindowMinS { get; init; }

        [JsonPropertyName("window_max_s")]
        public double WindowMaxS { get; init; }

        [JsonPropertyName("warmup")]
        public WarmupSettings Warmup { get; init; } = new();

        [JsonPropertyName("gate_ladder_s")]
        public double[] GateLadderS { get; init; } = Array.Empty<double>();

        [JsonPropertyName("covariance_regularization_epsilon")]
        public double CovarianceRegularizationEpsilon { get; init; }

        [JsonPropertyName("gate_stability")]
        public GateStabilitySettings GateStability { get; init; } = new();

        [JsonPropertyName("min_significance")]
        public double MinSignificance { get; init; }

        [JsonPropertyName("plateau")]
        public PlateauSettings Plateau { get; init; } = new();

        [JsonPropertyName("gate_change_confirm_steps")]
        public int GateChangeConfirmSteps { get; init; }

        [JsonPropertyName("window_adaptation")]
        public WindowAdaptationSettings WindowAdaptation { get; init; } = new();

        [JsonPropertyName("page_hinkley")]
        public PageHinkleyConfig PageHinkley { get; init; } = new();

        [JsonPropertyName("quiet_horizon")]
        public QuietHorizonSettings QuietHorizon { get; init; } = new();

        [JsonPropertyName("fsm_confirmation_steps")]
        public int FsmConfirmationSteps { get; init; }

        [JsonPropertyName("thresholds")]
        public ThresholdSettings Thresholds { get; init; } = new();

        public static RtReplayConfig Load(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException("Config path is required.", nameof(path));
            }

            if (!File.Exists(path))
            {
                throw new FileNotFoundException($"Config file not found: {path}", path);
            }

            var json = File.ReadAllText(path);
            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            };
            var config = JsonSerializer.Deserialize<RtReplayConfig>(json, options)
                         ?? throw new InvalidOperationException("Failed to deserialize replay config.");

            Validate(config);
            return config;
        }

        public static string ComputeHash(string path)
        {
            using var sha = SHA256.Create();
            var bytes = File.ReadAllBytes(path);
            var hash = sha.ComputeHash(bytes);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }

        private static void Validate(RtReplayConfig config)
        {
            if (string.IsNullOrWhiteSpace(config.Version))
            {
                throw new InvalidOperationException("Replay config must include a version string.");
            }

            RequirePositive(config.StepPeriodS, nameof(config.StepPeriodS));
            RequirePositive(config.BinWidthS, nameof(config.BinWidthS));
            RequirePositive(config.WindowStartS, nameof(config.WindowStartS));
            RequirePositive(config.WindowMinS, nameof(config.WindowMinS));
            RequirePositive(config.WindowMaxS, nameof(config.WindowMaxS));

            if (config.WindowMaxS < config.WindowMinS)
            {
                throw new InvalidOperationException("window_max_s must be >= window_min_s.");
            }

            if (config.GateLadderS.Length == 0)
            {
                throw new InvalidOperationException("gate_ladder_s must be non-empty.");
            }

            if (config.GateLadderS.Any(v => !double.IsFinite(v) || v <= 0))
            {
                throw new InvalidOperationException("gate_ladder_s entries must be finite and > 0.");
            }

            var sorted = config.GateLadderS.OrderBy(v => v).ToArray();
            if (!sorted.SequenceEqual(config.GateLadderS))
            {
                throw new InvalidOperationException("gate_ladder_s must be sorted ascending.");
            }

            double maxGate = config.GateLadderS.Max();
            if (config.WindowMaxS < maxGate)
            {
                throw new InvalidOperationException("window_max_s must be >= max(gate_ladder_s).");
            }

            RequirePositive(config.CovarianceRegularizationEpsilon, nameof(config.CovarianceRegularizationEpsilon));
            RequirePositive(config.MinSignificance, nameof(config.MinSignificance));
            RequirePositive(config.Plateau.EtaFraction, nameof(config.Plateau.EtaFraction));
            RequirePositive(config.WindowAdaptation.TargetRelY, nameof(config.WindowAdaptation.TargetRelY));
            RequirePositive(config.WindowAdaptation.TargetRelM1, nameof(config.WindowAdaptation.TargetRelM1));

            if (config.GateStability.MinGateIndexForZ < 0)
            {
                throw new InvalidOperationException("gate_stability.min_gate_index_for_z must be >= 0.");
            }

            if (config.GateStability.MinGateCountForZ < 0)
            {
                throw new InvalidOperationException("gate_stability.min_gate_count_for_z must be >= 0.");
            }

            RequirePositive(config.WindowAdaptation.RelUncertaintyShrinkFactor, nameof(config.WindowAdaptation.RelUncertaintyShrinkFactor));
            RequirePositive(config.WindowAdaptation.RelUncertaintyExpandFactor, nameof(config.WindowAdaptation.RelUncertaintyExpandFactor));
            RequirePositive(config.WindowAdaptation.PrecheckShrinkMultiplier, nameof(config.WindowAdaptation.PrecheckShrinkMultiplier));
            RequirePositive(config.WindowAdaptation.PrecheckExpandMultiplier, nameof(config.WindowAdaptation.PrecheckExpandMultiplier));
            RequirePositive(config.WindowAdaptation.ThresholdExpandMultiplier, nameof(config.WindowAdaptation.ThresholdExpandMultiplier));
            RequirePositive(config.WindowAdaptation.ThresholdShrinkMultiplier, nameof(config.WindowAdaptation.ThresholdShrinkMultiplier));

            RequirePositive(config.WindowAdaptation.RateLimitDefaultDown, nameof(config.WindowAdaptation.RateLimitDefaultDown));
            RequirePositive(config.WindowAdaptation.RateLimitDefaultUp, nameof(config.WindowAdaptation.RateLimitDefaultUp));
            RequirePositive(config.WindowAdaptation.RateLimitExpandDown, nameof(config.WindowAdaptation.RateLimitExpandDown));
            RequirePositive(config.WindowAdaptation.RateLimitExpandUp, nameof(config.WindowAdaptation.RateLimitExpandUp));
            RequirePositive(config.WindowAdaptation.RateLimitContractDown, nameof(config.WindowAdaptation.RateLimitContractDown));
            RequirePositive(config.WindowAdaptation.RateLimitContractUp, nameof(config.WindowAdaptation.RateLimitContractUp));

            RequirePositive(config.WindowAdaptation.WFloorS, nameof(config.WindowAdaptation.WFloorS));
            RequirePositive(config.WindowAdaptation.KTau, nameof(config.WindowAdaptation.KTau));

            if (config.WindowMaxS < config.WindowAdaptation.WFloorS)
            {
                throw new InvalidOperationException("window_max_s must be >= window_adaptation.w_floor_s.");
            }

            RequirePositive(config.PageHinkley.Singles.Delta, nameof(config.PageHinkley.Singles.Delta));
            RequirePositive(config.PageHinkley.Singles.Lambda, nameof(config.PageHinkley.Singles.Lambda));
            RequirePositive(config.PageHinkley.CorrZ.Delta, nameof(config.PageHinkley.CorrZ.Delta));
            RequirePositive(config.PageHinkley.CorrZ.Lambda, nameof(config.PageHinkley.CorrZ.Lambda));

            RequirePositive(config.QuietHorizon.StepMultiplier, nameof(config.QuietHorizon.StepMultiplier));
            RequirePositive(config.QuietHorizon.TauMultiplier, nameof(config.QuietHorizon.TauMultiplier));

            if (config.GateChangeConfirmSteps <= 0)
            {
                throw new InvalidOperationException("gate_change_confirm_steps must be >= 1.");
            }

            if (config.FsmConfirmationSteps <= 0)
            {
                throw new InvalidOperationException("fsm_confirmation_steps must be >= 1.");
            }

            if (config.Thresholds.ZHold < config.Thresholds.ZTrack)
            {
                throw new InvalidOperationException("z_hold must be >= z_track.");
            }
        }

        private static void RequirePositive(double value, string name)
        {
            if (!double.IsFinite(value) || value <= 0)
            {
                throw new InvalidOperationException($"{name} must be finite and > 0.");
            }
        }

        public sealed class WarmupSettings
        {
            [JsonPropertyName("require_window_filled")]
            public bool RequireWindowFilled { get; init; } = true;

            [JsonPropertyName("min_gate_count")]
            public int MinGateCount { get; init; } = 2;

            [JsonPropertyName("require_positive_m1")]
            public bool RequirePositiveM1 { get; init; } = true;
        }

        public sealed class GateStabilitySettings
        {
            [JsonPropertyName("min_gate_index_for_z")]
            public int MinGateIndexForZ { get; init; } = 2;

            [JsonPropertyName("min_gate_count_for_z")]
            public int MinGateCountForZ { get; init; }
        }

        public sealed class PlateauSettings
        {
            [JsonPropertyName("eta_fraction")]
            public double EtaFraction { get; init; } = 0.05;
        }

        public sealed class WindowAdaptationSettings
        {
            [JsonPropertyName("target_rel_y")]
            public double TargetRelY { get; init; } = 0.1;

            [JsonPropertyName("target_rel_m1")]
            public double TargetRelM1 { get; init; } = 0.02;

            [JsonPropertyName("rel_uncertainty_shrink_factor")]
            public double RelUncertaintyShrinkFactor { get; init; } = 0.5;

            [JsonPropertyName("rel_uncertainty_expand_factor")]
            public double RelUncertaintyExpandFactor { get; init; } = 2.0;

            [JsonPropertyName("precheck_shrink_multiplier")]
            public double PrecheckShrinkMultiplier { get; init; } = 0.9;

            [JsonPropertyName("precheck_expand_multiplier")]
            public double PrecheckExpandMultiplier { get; init; } = 1.2;

            [JsonPropertyName("threshold_expand_multiplier")]
            public double ThresholdExpandMultiplier { get; init; } = 1.3;

            [JsonPropertyName("threshold_shrink_multiplier")]
            public double ThresholdShrinkMultiplier { get; init; } = 0.95;

            [JsonPropertyName("rate_limit_default_down")]
            public double RateLimitDefaultDown { get; init; } = 0.5;

            [JsonPropertyName("rate_limit_default_up")]
            public double RateLimitDefaultUp { get; init; } = 2.0;

            [JsonPropertyName("rate_limit_expand_down")]
            public double RateLimitExpandDown { get; init; } = 0.8;

            [JsonPropertyName("rate_limit_expand_up")]
            public double RateLimitExpandUp { get; init; } = 2.0;

            [JsonPropertyName("rate_limit_contract_down")]
            public double RateLimitContractDown { get; init; } = 0.5;

            [JsonPropertyName("rate_limit_contract_up")]
            public double RateLimitContractUp { get; init; } = 1.2;

            [JsonPropertyName("w_floor_s")]
            public double WFloorS { get; init; } = 0.5;

            [JsonPropertyName("k_tau")]
            public double KTau { get; init; } = 3.0;
        }

        public sealed class PageHinkleyConfig
        {
            [JsonPropertyName("singles")]
            public PageHinkleySettings Singles { get; init; } = new();

            [JsonPropertyName("corr_z")]
            public PageHinkleySettings CorrZ { get; init; } = new();
        }

        public sealed class PageHinkleySettings
        {
            [JsonPropertyName("delta")]
            public double Delta { get; init; } = 0.005;

            [JsonPropertyName("lambda")]
            public double Lambda { get; init; } = 50.0;
        }

        public sealed class QuietHorizonSettings
        {
            [JsonPropertyName("step_multiplier")]
            public double StepMultiplier { get; init; } = 5.0;

            [JsonPropertyName("tau_multiplier")]
            public double TauMultiplier { get; init; } = 4.0;
        }

        public sealed class ThresholdSettings
        {
            [JsonPropertyName("z_track")]
            public double ZTrack { get; init; } = 3.0;

            [JsonPropertyName("z_hold")]
            public double ZHold { get; init; } = 6.0;

            [JsonPropertyName("z_poisson")]
            public double ZPoisson { get; init; } = 4.0;

            [JsonPropertyName("poisson_quiet_required")]
            public int PoissonQuietRequired { get; init; } = 1;

            [JsonPropertyName("low_rate_gate_index")]
            public int LowRateGateIndex { get; init; }

            [JsonPropertyName("degraded_gate_index")]
            public int DegradedGateIndex { get; init; }

            [JsonPropertyName("low_rate_window_growth_factor")]
            public double LowRateWindowGrowthFactor { get; init; } = 1.2;
        }
    }
}
