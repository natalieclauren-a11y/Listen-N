using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Listen_N
{
    public static class RtReplayRunner
    {
        public static int RunCli(string[] args)
        {
            string configPath = Path.Combine(Directory.GetCurrentDirectory(), "configs", "rt_replay_v1.json");
            string? inputPath = null;
            string outputRoot = Path.Combine(Directory.GetCurrentDirectory(), "artifacts");
            string? runId = null;
            DateTimeOffset startUtc = DateTimeOffset.UnixEpoch;

            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];
                switch (arg)
                {
                    case "--config" when i + 1 < args.Length:
                        configPath = args[++i];
                        break;
                    case "--input" when i + 1 < args.Length:
                        inputPath = args[++i];
                        break;
                    case "--output" when i + 1 < args.Length:
                        outputRoot = args[++i];
                        break;
                    case "--run-id" when i + 1 < args.Length:
                        runId = args[++i];
                        break;
                    case "--start-utc" when i + 1 < args.Length:
                        startUtc = DateTimeOffset.Parse(args[++i], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
                        break;
                    case "--help":
                    case "-h":
                        PrintUsage();
                        return 0;
                }
            }

            if (string.IsNullOrWhiteSpace(inputPath))
            {
                Console.Error.WriteLine("Replay requires --input <list-mode file>.");
                PrintUsage();
                return 1;
            }

            var config = RtReplayConfig.Load(configPath);
            var detections = LoadDetections(inputPath);
            if (detections.Count == 0)
            {
                Console.Error.WriteLine("Replay input contained no detections.");
                return 1;
            }

            string configHash = RtReplayConfig.ComputeHash(configPath);
            if (string.IsNullOrWhiteSpace(runId))
            {
                runId = BuildRunId(configHash, inputPath);
            }

            RunReplay(config, detections, outputRoot, runId, startUtc, configHash);
            return 0;
        }

        private static void PrintUsage()
        {
            Console.WriteLine("Usage: Listen-N replay --input <list-mode file> [--config <path>] [--output <dir>] [--run-id <id>] [--start-utc <iso8601>]");
        }

        private static string BuildRunId(string configHash, string inputPath)
        {
            using var sha = SHA256.Create();
            byte[] inputBytes = File.ReadAllBytes(inputPath);
            byte[] configBytes = Encoding.UTF8.GetBytes(configHash);
            byte[] combined = new byte[inputBytes.Length + configBytes.Length];
            Buffer.BlockCopy(inputBytes, 0, combined, 0, inputBytes.Length);
            Buffer.BlockCopy(configBytes, 0, combined, inputBytes.Length, configBytes.Length);
            var hash = sha.ComputeHash(combined);
            return $"replay_{Convert.ToHexString(hash).Substring(0, 12).ToLowerInvariant()}";
        }

        private static List<Detection> LoadDetections(string path)
        {
            var detections = new List<Detection>();
            long lastTs = long.MinValue;

            foreach (string line in File.ReadLines(path))
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0 || trimmed.StartsWith('#'))
                {
                    continue;
                }

                string[] parts = trimmed.Split(new[] { ',', '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0)
                {
                    continue;
                }

                if (!TryParseTimestamp(parts[0], out long tUs))
                {
                    throw new InvalidOperationException($"Unable to parse timestamp: '{parts[0]}'");
                }

                if (tUs < 0)
                {
                    throw new InvalidOperationException("Timestamps must be non-negative microseconds.");
                }

                byte detId = 0;
                if (parts.Length > 1 && byte.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                {
                    detId = parsed;
                }

                if (tUs < lastTs)
                {
                    throw new InvalidOperationException("List-mode data must be monotonically non-decreasing.");
                }

                detections.Add(new Detection(tUs, detId));
                lastTs = tUs;
            }

            return detections;
        }

        private static bool TryParseTimestamp(string token, out long tUs)
        {
            if (long.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out tUs))
            {
                return true;
            }

            if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds))
            {
                tUs = (long)Math.Round(seconds * 1e6);
                return true;
            }

            return false;
        }

        private static void RunReplay(
            RtReplayConfig config,
            IReadOnlyList<Detection> detections,
            string outputRoot,
            string runId,
            DateTimeOffset startUtc,
            string configHash)
        {
            string runDir = Path.Combine(outputRoot, runId);
            Directory.CreateDirectory(runDir);

            string windowsPath = Path.Combine(runDir, "rt_windows.ndjson");
            string summaryPath = Path.Combine(runDir, "replay_summary.json");

            var gateLadder = config.GateLadderS.ToArray();
            var excludedInvalid = new int[gateLadder.Length];
            var excludedByIndex = new int[gateLadder.Length];
            var holdTransitions = new List<HoldTransition>();

            using var engine = new AdaptiveWindowEngine(config, startWorker: false, enableFileLog: false);

            long firstUs = detections[0].TicksUs;
            long lastUs = detections[^1].TicksUs;
            long stepPeriodUs = (long)Math.Round(config.StepPeriodS * 1e6);
            long windowStartUs = (long)Math.Round(config.WindowStartS * 1e6);
            long stepStartUs = firstUs + windowStartUs;
            if (stepStartUs <= firstUs)
            {
                stepStartUs = firstUs + stepPeriodUs;
            }

            engine.ResetTimestampState(firstUs);

            bool wasHold = false;
            DateTimeOffset? holdEnter = null;
            int stepsEmitted = 0;

            var jsonOptions = new JsonSerializerOptions
            {
                WriteIndented = false,
                NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
            };

            using var writer = new StreamWriter(windowsPath, append: false, Encoding.UTF8);

            engine.OnReplayStep += step =>
            {
                long relativeUs = step.NowUs - firstUs;
                DateTimeOffset windowEndUtc = startUtc + TimeSpan.FromTicks(relativeUs * 10);
                DateTimeOffset windowStartUtc = windowEndUtc.AddSeconds(-step.WindowSec);

                for (int i = 0; i < gateLadder.Length && i < step.GateValid.Length; i++)
                {
                    if (!step.GateValid[i])
                    {
                        excludedInvalid[i]++;
                    }

                    if (i < config.GateStability.MinGateIndexForZ)
                    {
                        excludedByIndex[i]++;
                    }
                }

                bool isHold = step.HoldFlag;
                if (isHold && !wasHold)
                {
                    holdEnter = windowEndUtc;
                }
                else if (!isHold && wasHold && holdEnter.HasValue)
                {
                    holdTransitions.Add(new HoldTransition(holdEnter.Value, windowEndUtc));
                    holdEnter = null;
                }
                wasHold = isHold;

                var record = new RtReplayStepRecord
                {
                    WindowStartUtc = windowStartUtc,
                    WindowEndUtc = windowEndUtc,
                    WindowS = step.WindowSec,
                    SelectedGateS = step.SelectedGateUs / 1e6,
                    SelectedGateIndex = step.SelectedGateIndex,
                    GateLadderS = gateLadder,
                    GateY = step.GateY,
                    GateSigmaY = step.GateSigmaY,
                    GateM1 = step.GateM1,
                    GateM2 = step.GateM2,
                    GateM3 = step.GateM3,
                    GateVarM1 = step.GateVarM1,
                    GateVarM2 = step.GateVarM2,
                    GateVarM3 = step.GateVarM3,
                    GateCovM1M2 = step.GateCovM1M2,
                    GateCovM1M3 = step.GateCovM1M3,
                    GateCovM2M3 = step.GateCovM2M3,
                    GateValid = step.GateValid,
                    GateSignificant = step.GateSignificant,
                    SignificantGateIndex = step.SignificantGateIndex,
                    PlateauGateIndex = step.PlateauGateIndex,
                    PendingGateIndex = step.PendingGateIndex,
                    GateConfirmations = step.GateConfirmations,
                    GateConfirmRequired = step.GateConfirmRequired,
                    GateFrozenInHold = step.GateFrozenInHold,
                    PageHinkleyAlarm = step.PageHinkleyAlarm,
                    SinglesChange = step.SinglesChange,
                    CorrelationChange = step.CorrelationChange,
                    PageHinkleyMean = step.PageHinkleyMean,
                    PageHinkleyCum = step.PageHinkleyCum,
                    PageHinkleyZyMean = step.PageHinkleyZyMean,
                    PageHinkleyZyCum = step.PageHinkleyZyCum,
                    FsmState = step.State,
                    HoldFlag = step.HoldFlag,
                    TauHatSec = step.TauHatSec,
                    CorrFitRms = step.CorrFitRms,
                    CorrResiduals = step.CorrResiduals,
                    IllConditionedCovariance = step.IllConditionedCovariance,
                    InsufficientStatistics = step.InsufficientStatistics,
                    ModelMismatch = step.ModelMismatch
                };

                string json = JsonSerializer.Serialize(record, jsonOptions);
                writer.WriteLine(json);
                stepsEmitted++;
            };

            int detectionIndex = 0;
            long finalStepUs = Math.Max(lastUs, stepStartUs);
            for (long nowUs = stepStartUs; nowUs <= finalStepUs; nowUs += stepPeriodUs)
            {
                while (detectionIndex < detections.Count && detections[detectionIndex].TicksUs <= nowUs)
                {
                    engine.OnDetection(detections[detectionIndex]);
                    detectionIndex++;
                }

                engine.RunDeterministicStep(nowUs, config.StepPeriodS);
            }

            if (holdEnter.HasValue)
            {
                holdTransitions.Add(new HoldTransition(holdEnter.Value, startUtc + TimeSpan.FromTicks((finalStepUs - firstUs) * 10)));
            }

            var summary = new ReplaySummary
            {
                ConfigVersion = config.Version,
                ConfigHash = configHash,
                CountsProcessed = detections.Count,
                StepsEmitted = stepsEmitted,
                ExcludedGates = new GateExclusionSummary
                {
                    InvalidGateCounts = excludedInvalid,
                    ExcludedByIndexCounts = excludedByIndex
                },
                HoldTransitions = holdTransitions
            };

            File.WriteAllText(summaryPath, JsonSerializer.Serialize(summary, jsonOptions));
        }

        private sealed class RtReplayStepRecord
        {
            [JsonPropertyName("window_start_utc")]
            public DateTimeOffset WindowStartUtc { get; init; }

            [JsonPropertyName("window_end_utc")]
            public DateTimeOffset WindowEndUtc { get; init; }

            [JsonPropertyName("window_s")]
            public double WindowS { get; init; }

            [JsonPropertyName("selected_gate_s")]
            public double SelectedGateS { get; init; }

            [JsonPropertyName("selected_gate_index")]
            public int SelectedGateIndex { get; init; }

            [JsonPropertyName("gate_ladder_s")]
            public double[] GateLadderS { get; init; } = Array.Empty<double>();

            [JsonPropertyName("gate_y")]
            public double[] GateY { get; init; } = Array.Empty<double>();

            [JsonPropertyName("gate_sigma_y")]
            public double[] GateSigmaY { get; init; } = Array.Empty<double>();

            [JsonPropertyName("gate_m1")]
            public double[] GateM1 { get; init; } = Array.Empty<double>();

            [JsonPropertyName("gate_m2")]
            public double[] GateM2 { get; init; } = Array.Empty<double>();

            [JsonPropertyName("gate_m3")]
            public double[] GateM3 { get; init; } = Array.Empty<double>();

            [JsonPropertyName("gate_var_m1")]
            public double[] GateVarM1 { get; init; } = Array.Empty<double>();

            [JsonPropertyName("gate_var_m2")]
            public double[] GateVarM2 { get; init; } = Array.Empty<double>();

            [JsonPropertyName("gate_var_m3")]
            public double[] GateVarM3 { get; init; } = Array.Empty<double>();

            [JsonPropertyName("gate_cov_m1_m2")]
            public double[] GateCovM1M2 { get; init; } = Array.Empty<double>();

            [JsonPropertyName("gate_cov_m1_m3")]
            public double[] GateCovM1M3 { get; init; } = Array.Empty<double>();

            [JsonPropertyName("gate_cov_m2_m3")]
            public double[] GateCovM2M3 { get; init; } = Array.Empty<double>();

            [JsonPropertyName("gate_valid")]
            public bool[] GateValid { get; init; } = Array.Empty<bool>();

            [JsonPropertyName("gate_significant")]
            public bool[] GateSignificant { get; init; } = Array.Empty<bool>();

            [JsonPropertyName("significant_gate_index")]
            public int SignificantGateIndex { get; init; }

            [JsonPropertyName("plateau_gate_index")]
            public int PlateauGateIndex { get; init; }

            [JsonPropertyName("pending_gate_index")]
            public int PendingGateIndex { get; init; }

            [JsonPropertyName("gate_confirmations")]
            public int GateConfirmations { get; init; }

            [JsonPropertyName("gate_confirm_required")]
            public int GateConfirmRequired { get; init; }

            [JsonPropertyName("gate_frozen_in_hold")]
            public bool GateFrozenInHold { get; init; }

            [JsonPropertyName("page_hinkley_alarm")]
            public bool PageHinkleyAlarm { get; init; }

            [JsonPropertyName("singles_change")]
            public bool SinglesChange { get; init; }

            [JsonPropertyName("correlation_change")]
            public bool CorrelationChange { get; init; }

            [JsonPropertyName("page_hinkley_mean")]
            public double PageHinkleyMean { get; init; }

            [JsonPropertyName("page_hinkley_cum")]
            public double PageHinkleyCum { get; init; }

            [JsonPropertyName("page_hinkley_zy_mean")]
            public double PageHinkleyZyMean { get; init; }

            [JsonPropertyName("page_hinkley_zy_cum")]
            public double PageHinkleyZyCum { get; init; }

            [JsonPropertyName("fsm_state")]
            public string FsmState { get; init; } = string.Empty;

            [JsonPropertyName("hold_flag")]
            public bool HoldFlag { get; init; }

            [JsonPropertyName("tau_hat_s")]
            public double TauHatSec { get; init; }

            [JsonPropertyName("correlation_fit_rms")]
            public double CorrFitRms { get; init; }

            [JsonPropertyName("correlation_residuals")]
            public double[] CorrResiduals { get; init; } = Array.Empty<double>();

            [JsonPropertyName("ill_conditioned_covariance")]
            public bool IllConditionedCovariance { get; init; }

            [JsonPropertyName("insufficient_statistics")]
            public bool InsufficientStatistics { get; init; }

            [JsonPropertyName("model_mismatch")]
            public bool ModelMismatch { get; init; }
        }

        private sealed record HoldTransition(
            [property: JsonPropertyName("hold_enter_utc")] DateTimeOffset HoldEnterUtc,
            [property: JsonPropertyName("hold_exit_utc")] DateTimeOffset HoldExitUtc);

        private sealed class GateExclusionSummary
        {
            [JsonPropertyName("invalid_gate_counts")]
            public int[] InvalidGateCounts { get; init; } = Array.Empty<int>();

            [JsonPropertyName("excluded_by_index_counts")]
            public int[] ExcludedByIndexCounts { get; init; } = Array.Empty<int>();
        }

        private sealed class ReplaySummary
        {
            [JsonPropertyName("config_version")]
            public string ConfigVersion { get; init; } = string.Empty;

            [JsonPropertyName("config_hash")]
            public string ConfigHash { get; init; } = string.Empty;

            [JsonPropertyName("counts_processed")]
            public int CountsProcessed { get; init; }

            [JsonPropertyName("steps_emitted")]
            public int StepsEmitted { get; init; }

            [JsonPropertyName("excluded_gates")]
            public GateExclusionSummary ExcludedGates { get; init; } = new();

            [JsonPropertyName("hold_transitions")]
            public List<HoldTransition> HoldTransitions { get; init; } = new();
        }
    }
}
