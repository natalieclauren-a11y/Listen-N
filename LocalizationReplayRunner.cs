using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using Integrated.Contracts;
using Integrated.Runtime;
using RtLocalizationRequest = Integrated.Runtime.LocalizationRequest;

namespace Listen_N
{
    public sealed record LocalizationReplayOptions
    {
        public required string InputPath { get; init; }
        public required string OutputDirectory { get; init; }
        public string? PolicyPath { get; init; }
        public string? RunId { get; init; }
    }

    public sealed record LocalizationReplayDependencies
    {
        public required LocalizationWorker Worker { get; init; }
        public required LocalizationEpisodePolicyConfig EpisodePolicyConfig { get; init; }
        public required TriggerPolicyThresholds Thresholds { get; init; }
        public required LocalizationRuntimeConfig RuntimeConfig { get; init; }
    }

    public static class LocalizationReplayRunner
    {
        private static readonly JsonSerializerOptions EventSerializerOptions = new()
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
            WriteIndented = false
        };

        public static int RunCli(string[] args)
        {
            string? inputPath = null;
            string? outputDir = null;
            string? policyPath = null;
            string? runId = null;

            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];
                switch (arg)
                {
                    case "--input" when i + 1 < args.Length:
                        inputPath = args[++i];
                        break;
                    case "--output" when i + 1 < args.Length:
                        outputDir = args[++i];
                        break;
                    case "--policy" when i + 1 < args.Length:
                        policyPath = args[++i];
                        break;
                    case "--run-id" when i + 1 < args.Length:
                        runId = args[++i];
                        break;
                    case "--help":
                    case "-h":
                        PrintUsage();
                        return 0;
                }
            }

            if (string.IsNullOrWhiteSpace(inputPath) || string.IsNullOrWhiteSpace(outputDir))
            {
                Console.Error.WriteLine("localize requires --input <rt_windows.ndjson> and --output <dir>.");
                PrintUsage();
                return 1;
            }

            var options = new LocalizationReplayOptions
            {
                InputPath = inputPath,
                OutputDirectory = outputDir,
                PolicyPath = policyPath,
                RunId = runId
            };

            return Run(options, dependencies: null);
        }

        public static int Run(LocalizationReplayOptions options, LocalizationReplayDependencies? dependencies)
        {
            if (options is null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            if (string.IsNullOrWhiteSpace(options.InputPath))
            {
                Console.Error.WriteLine("localize requires --input <rt_windows.ndjson>.");
                return 1;
            }

            if (!File.Exists(options.InputPath))
            {
                Console.Error.WriteLine($"Input file not found: {options.InputPath}");
                return 1;
            }

            if (string.IsNullOrWhiteSpace(options.OutputDirectory))
            {
                Console.Error.WriteLine("localize requires --output <dir>.");
                return 1;
            }

            try
            {
                Directory.CreateDirectory(options.OutputDirectory);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Failed to create output directory: {ex.Message}");
                return 1;
            }

            string runId = ResolveRunId(options.RunId, options.OutputDirectory);
            if (string.IsNullOrWhiteSpace(runId))
            {
                Console.Error.WriteLine("Unable to determine run id.");
                return 1;
            }

            LocalizationReplayDependencies resolvedDependencies;
            try
            {
                resolvedDependencies = dependencies ?? LoadDependencies(options.PolicyPath);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Localization artifacts missing or invalid: {ex.Message}");
                return 1;
            }

            var triggerPolicy = new DefaultLocalizationTriggerPolicy();
            var policy = new LocalizationEpisodePolicy(resolvedDependencies.EpisodePolicyConfig, resolvedDependencies.Thresholds)
            {
                AutoModeEnabled = true,
                RunId = ResolveRunGuid(runId)
            };

            var worker = resolvedDependencies.Worker;
            var health = new LocalizationHealthTracker(
                resolvedDependencies.RuntimeConfig.DegradedRefusalThreshold,
                resolvedDependencies.RuntimeConfig.DegradedQueueSaturationThreshold);
            var limiter = new LocalizationMlRequestLimiter(worker, policy, resolvedDependencies.RuntimeConfig, health);

            object writeLock = new();
            int windowsProcessed = 0;
            int episodesStarted = 0;
            int episodesCompleted = 0;
            int episodesRefused = 0;
            int pendingRequests = 0;
            bool workerFaulted = false;
            Exception? workerException = null;
            DateTimeOffset? currentWindowEndUtc = null;

            var lastRequestByEpisode = new Dictionary<Guid, RtLocalizationRequest>();

            string eventsPath = Path.Combine(options.OutputDirectory, "localization_events.ndjson");
            StreamWriter? writer = null;
            try
            {
                writer = new StreamWriter(eventsPath, append: false);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Failed to write localization event log: {ex.Message}");
                return 1;
            }

            using (writer)
            {
                using var cts = new CancellationTokenSource();
                Task workerTask = worker.Start(cts.Token);

                policy.OnRequestMl += request =>
                {
                    lock (lastRequestByEpisode)
                    {
                        lastRequestByEpisode[request.EpisodeId] = request;
                    }

                    Interlocked.Increment(ref pendingRequests);
                    limiter.HandleRequest(request);
                };

                worker.OnResult += (request, prediction) =>
                {
                    policy.OnMlResult(request, prediction);
                    Interlocked.Decrement(ref pendingRequests);
                };

                worker.OnWorkerFaulted += ex =>
                {
                    workerFaulted = true;
                    workerException = ex;
                };

                policy.NowProvider = () => currentWindowEndUtc ?? DateTimeOffset.MinValue;

                policy.OnDecisionRecord += record =>
                {
                    if (record.DecisionKind == LocalizationDecisionKind.EpisodeStart)
                    {
                        episodesStarted++;
                        var evt = BuildEvent(
                            "episode_started",
                            runId,
                            currentWindowEndUtc ?? DateTimeOffset.MinValue,
                            currentWindowEndUtc ?? DateTimeOffset.MinValue,
                            reason: null,
                            requestedDurationSeconds: record.EpisodeContext.AccumulatedDurationSeconds,
                            result: null,
                            record);
                        WriteEvent(writer, writeLock, evt);
                        return;
                    }

                    if (record.DecisionKind == LocalizationDecisionKind.Publish)
                    {
                        episodesCompleted++;
                        var evt = BuildEvent(
                            "episode_completed",
                            runId,
                            ResolveWindowEndUtc(record.EpisodeId, currentWindowEndUtc, lastRequestByEpisode),
                            ResolveWindowEndUtc(record.EpisodeId, currentWindowEndUtc, lastRequestByEpisode),
                            reason: null,
                            requestedDurationSeconds: null,
                            result: record.Result.PublishedCoords,
                            record);
                        WriteEvent(writer, writeLock, evt);
                        return;
                    }

                    if (record.DecisionKind == LocalizationDecisionKind.Refuse)
                    {
                        episodesRefused++;
                        var evt = BuildEvent(
                            "episode_refused",
                            runId,
                            ResolveWindowEndUtc(record.EpisodeId, currentWindowEndUtc, lastRequestByEpisode),
                            ResolveWindowEndUtc(record.EpisodeId, currentWindowEndUtc, lastRequestByEpisode),
                            reason: record.ReasonCode.ToString(),
                            requestedDurationSeconds: null,
                            result: null,
                            record);
                        WriteEvent(writer, writeLock, evt);
                    }
                };

                try
                {
                    foreach (string line in File.ReadLines(options.InputPath))
                    {
                        if (string.IsNullOrWhiteSpace(line))
                        {
                            continue;
                        }

                        RtWindowSummary window = ParseWindowSummary(line);
                        windowsProcessed++;
                        currentWindowEndUtc = window.WindowEndUtc;

                        var snapshot = BuildSnapshot(window, windowsProcessed);
                        bool triggered = triggerPolicy.ShouldTrigger(snapshot);
                        var triggerEvent = new LocalizationReplayEvent
                        {
                            EventTimeUtc = window.WindowEndUtc,
                            EventType = "trigger_evaluated",
                            RunId = runId,
                            WindowEndUtc = window.WindowEndUtc,
                            Triggered = triggered,
                            RtState = window.RtState
                        };
                        WriteEvent(writer, writeLock, triggerEvent);

                        policy.AddWindow(window);

                        if (workerFaulted)
                        {
                            throw new InvalidOperationException(workerException?.Message ?? "Localization worker faulted.");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Failed to process localization replay: {ex.Message}");
                    return 1;
                }
                finally
                {
                    WaitForPendingRequests(ref pendingRequests, workerFaulted);
                    cts.Cancel();
                    AwaitWorker(workerTask);
                }
            }

            Console.WriteLine($"windows_processed={windowsProcessed}, episodes_started={episodesStarted}, episodes_completed={episodesCompleted}, episodes_refused={episodesRefused}");
            return workerFaulted ? 1 : 0;
        }

        private static LocalizationReplayDependencies LoadDependencies(string? policyPath)
        {
            string baseDirectory = Directory.GetCurrentDirectory();
            string runtimeConfigPath = Path.Combine(baseDirectory, "localization_runtime_config.json");
            var runtimeConfig = LocalizationRuntimeConfig.Load(runtimeConfigPath);
            var validation = runtimeConfig.Validate(baseDirectory);
            if (!validation.IsValid)
            {
                throw new InvalidOperationException(string.Join("; ", validation.Errors));
            }

            string artifactsDirectory = validation.ResolvedArtifactsDirectory ?? runtimeConfig.ArtifactsDirectory;
            var artifacts = LocalizationArtifactsLoader.Load(artifactsDirectory);

            TriggerPolicyThresholds thresholds = artifacts.Thresholds;
            LocalizationEpisodePolicyConfig config = artifacts.PolicyConfig;
            if (!string.IsNullOrWhiteSpace(policyPath))
            {
                if (!File.Exists(policyPath))
                {
                    throw new InvalidOperationException($"Trigger policy not found at {policyPath}.");
                }

                var policyDocument = JsonSerializer.Deserialize<TriggerPolicyDocument>(
                    File.ReadAllText(policyPath),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                    ?? throw new InvalidOperationException("Trigger policy deserialized to null.");
                (thresholds, config) = policyDocument.ToPolicyInputs();
            }

            var worker = new LocalizationWorker(artifacts.Pipeline, runtimeConfig.MaxMlQueueDepth);
            return new LocalizationReplayDependencies
            {
                Worker = worker,
                EpisodePolicyConfig = config,
                Thresholds = thresholds,
                RuntimeConfig = runtimeConfig
            };
        }

        private static AnalysisSnapshot BuildSnapshot(RtWindowSummary window, int index)
        {
            var counts = new int[window.Counts15.Length];
            for (int i = 0; i < window.Counts15.Length; i++)
            {
                counts[i] = (int)Math.Round(window.Counts15[i]);
            }

            int totalCounts = (int)Math.Round(window.Counts15.Sum());
            return new AnalysisSnapshot(
                BuildDeterministicGuid(index, window.WindowEndUtc),
                window.WindowEndUtc.UtcDateTime,
                window.DurationSeconds,
                counts,
                totalCounts,
                window.RtState,
                0,
                0,
                0,
                window.QualityScalar,
                false,
                false,
                false,
                0,
                string.Empty,
                null);
        }

        private static RtWindowSummary ParseWindowSummary(string line)
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;

            DateTimeOffset windowStartUtc = GetDateTimeOffset(root, "window_start_utc", "windowStartUtc", "window_start", "windowStart");
            DateTimeOffset windowEndUtc = GetDateTimeOffset(root, "window_end_utc", "windowEndUtc", "window_end", "windowEnd");
            double duration = GetDouble(root, "duration_seconds", "durationSeconds", "window_s", "windowS", "window_sec", "windowSec");
            double[] counts = GetDoubleArray(root, "counts15", "counts_15", "counts");
            if (counts.Length != 15)
            {
                throw new InvalidOperationException("Expected counts array with 15 entries.");
            }

            string rtState = GetString(root, "rt_state", "rtState", "fsm_state", "fsmState");
            double qualityScalar = TryGetDouble(root, "quality_scalar", "qualityScalar", "zy") ?? double.NaN;
            double rateTotalCps = TryGetDouble(root, "rate_total_cps", "rateTotalCps")
                ?? (duration > 0 ? counts.Sum() / duration : 0.0);
            bool isConfusedCandidate = TryGetBool(root, "is_confused_candidate", "isConfusedCandidate") ?? false;

            return new RtWindowSummary
            {
                WindowStartUtc = windowStartUtc,
                WindowEndUtc = windowEndUtc,
                DurationSeconds = duration,
                Counts15 = counts,
                RtState = rtState,
                RateTotalCps = rateTotalCps,
                QualityScalar = qualityScalar,
                IsConfusedCandidate = isConfusedCandidate
            };
        }

        private static string ResolveRunId(string? runId, string outputDirectory)
        {
            if (!string.IsNullOrWhiteSpace(runId))
            {
                return runId;
            }

            string? derived = Path.GetFileName(Path.TrimEndingDirectorySeparator(outputDirectory));
            return string.IsNullOrWhiteSpace(derived) ? "localize" : derived;
        }

        private static Guid ResolveRunGuid(string runId)
        {
            if (Guid.TryParse(runId, out var parsed))
            {
                return parsed;
            }

            using var sha = System.Security.Cryptography.SHA256.Create();
            var bytes = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(runId));
            var guidBytes = new byte[16];
            Array.Copy(bytes, guidBytes, guidBytes.Length);
            return new Guid(guidBytes);
        }

        private static Guid BuildDeterministicGuid(int index, DateTimeOffset windowEndUtc)
        {
            var bytes = new byte[16];
            BitConverter.GetBytes(index).CopyTo(bytes, 0);
            BitConverter.GetBytes(windowEndUtc.UtcTicks).CopyTo(bytes, 4);
            return new Guid(bytes);
        }

        private static DateTimeOffset ResolveWindowEndUtc(
            Guid episodeId,
            DateTimeOffset? fallback,
            Dictionary<Guid, RtLocalizationRequest> lastRequestByEpisode)
        {
            lock (lastRequestByEpisode)
            {
                if (lastRequestByEpisode.TryGetValue(episodeId, out var request)
                    && request.Row.WindowEndUtc.HasValue)
                {
                    return request.Row.WindowEndUtc.Value;
                }
            }

            return fallback ?? DateTimeOffset.MinValue;
        }

        private static LocalizationReplayEvent BuildEvent(
            string eventType,
            string runId,
            DateTimeOffset eventTimeUtc,
            DateTimeOffset windowEndUtc,
            string? reason,
            double? requestedDurationSeconds,
            IReadOnlyList<double>? result,
            LocalizationDecisionRecord record)
        {
            double? resultX = null;
            double? resultY = null;
            double? resultZ = null;
            if (result != null && result.Count >= 3)
            {
                resultX = result[0];
                resultY = result[1];
                resultZ = result[2];
            }

            return new LocalizationReplayEvent
            {
                EventTimeUtc = eventTimeUtc,
                EventType = eventType,
                RunId = runId,
                WindowEndUtc = windowEndUtc,
                Reason = reason,
                RequestedDurationSeconds = requestedDurationSeconds,
                ResultX = resultX,
                ResultY = resultY,
                ResultZ = resultZ,
                Confidence = record.MlContext?.ClassifierProbability,
                ModelId = record.MlContext?.ModelId,
                OutcomeLabel = record.MlContext?.OutcomeLabel
            };
        }

        private static void WriteEvent(StreamWriter writer, object writeLock, LocalizationReplayEvent evt)
        {
            lock (writeLock)
            {
                string json = JsonSerializer.Serialize(evt, EventSerializerOptions);
                writer.WriteLine(json);
                writer.Flush();
            }
        }

        private static void WaitForPendingRequests(ref int pendingRequests, bool workerFaulted)
        {
            if (workerFaulted)
            {
                return;
            }

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            while (Interlocked.CompareExchange(ref pendingRequests, 0, 0) > 0)
            {
                if (stopwatch.Elapsed > TimeSpan.FromSeconds(10))
                {
                    break;
                }

                Thread.Sleep(10);
            }
        }

        private static void AwaitWorker(Task workerTask)
        {
            try
            {
                workerTask.GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
            }
        }

        private static DateTimeOffset GetDateTimeOffset(JsonElement root, params string[] names)
        {
            foreach (var name in names)
            {
                if (root.TryGetProperty(name, out var property))
                {
                    if (property.ValueKind == JsonValueKind.String &&
                        DateTimeOffset.TryParse(property.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
                    {
                        return parsed;
                    }

                    if (property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out var ticks))
                    {
                        return new DateTimeOffset(ticks, TimeSpan.Zero);
                    }
                }
            }

            throw new InvalidOperationException("Missing required datetime offset property.");
        }

        private static double GetDouble(JsonElement root, params string[] names)
        {
            var value = TryGetDouble(root, names);
            if (value.HasValue)
            {
                return value.Value;
            }

            throw new InvalidOperationException("Missing required numeric property.");
        }

        private static double? TryGetDouble(JsonElement root, params string[] names)
        {
            foreach (var name in names)
            {
                if (root.TryGetProperty(name, out var property))
                {
                    if (property.ValueKind == JsonValueKind.Number && property.TryGetDouble(out var value))
                    {
                        return value;
                    }

                    if (property.ValueKind == JsonValueKind.String
                        && double.TryParse(property.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
                    {
                        return parsed;
                    }
                }
            }

            return null;
        }

        private static string GetString(JsonElement root, params string[] names)
        {
            foreach (var name in names)
            {
                if (root.TryGetProperty(name, out var property))
                {
                    var value = property.GetString();
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        return value;
                    }
                }
            }

            throw new InvalidOperationException("Missing required string property.");
        }

        private static bool? TryGetBool(JsonElement root, params string[] names)
        {
            foreach (var name in names)
            {
                if (root.TryGetProperty(name, out var property))
                {
                    if (property.ValueKind == JsonValueKind.True)
                    {
                        return true;
                    }

                    if (property.ValueKind == JsonValueKind.False)
                    {
                        return false;
                    }

                    if (property.ValueKind == JsonValueKind.String
                        && bool.TryParse(property.GetString(), out var parsed))
                    {
                        return parsed;
                    }
                }
            }

            return null;
        }

        private static double[] GetDoubleArray(JsonElement root, params string[] names)
        {
            foreach (var name in names)
            {
                if (!root.TryGetProperty(name, out var property))
                {
                    continue;
                }

                if (property.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                var values = new double[property.GetArrayLength()];
                int idx = 0;
                foreach (var element in property.EnumerateArray())
                {
                    if (element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out var value))
                    {
                        values[idx++] = value;
                    }
                    else if (element.ValueKind == JsonValueKind.String
                        && double.TryParse(element.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
                    {
                        values[idx++] = parsed;
                    }
                    else
                    {
                        throw new InvalidOperationException("Counts array contains non-numeric entry.");
                    }
                }

                return values;
            }

            throw new InvalidOperationException("Missing required counts array.");
        }

        private static void PrintUsage()
        {
            Console.WriteLine("Usage: Listen-N localize --input <rt_windows.ndjson> --output <dir> [--policy <path>] [--run-id <id>]");
        }

        private sealed record LocalizationReplayEvent
        {
            [JsonPropertyName("event_time_utc")]
            public required DateTimeOffset EventTimeUtc { get; init; }

            [JsonPropertyName("event_type")]
            public required string EventType { get; init; }

            [JsonPropertyName("run_id")]
            public required string RunId { get; init; }

            [JsonPropertyName("window_end_utc")]
            public required DateTimeOffset WindowEndUtc { get; init; }

            [JsonPropertyName("reason")]
            public string? Reason { get; init; }

            [JsonPropertyName("requested_duration_s")]
            public double? RequestedDurationSeconds { get; init; }

            [JsonPropertyName("result_x")]
            public double? ResultX { get; init; }

            [JsonPropertyName("result_y")]
            public double? ResultY { get; init; }

            [JsonPropertyName("result_z")]
            public double? ResultZ { get; init; }

            [JsonPropertyName("confidence")]
            public double? Confidence { get; init; }

            [JsonPropertyName("model_id")]
            public string? ModelId { get; init; }

            [JsonPropertyName("outcome_label")]
            public string? OutcomeLabel { get; init; }

            [JsonPropertyName("triggered")]
            public bool? Triggered { get; init; }

            [JsonPropertyName("rt_state")]
            public string? RtState { get; init; }
        }

    }
}
