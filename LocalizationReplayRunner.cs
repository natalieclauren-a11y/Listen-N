using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
        public string? ModelsDirectory { get; init; }
        public string? ConfigPath { get; init; }
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
        private const string EventSchemaVersion = "loc_events.v1";
        private const string SummarySchemaVersion = "loc_replay_summary.v1";

        public static int RunCli(string[] args)
        {
            string? inputPath = null;
            string? outputDir = null;
            string? modelsDirectory = null;
            string? configPath = null;
            string? runId = null;

            int argIndex = 0;
            if (args.Length > 0 && (string.Equals(args[0], "replay", StringComparison.OrdinalIgnoreCase)
                || string.Equals(args[0], "--replay", StringComparison.OrdinalIgnoreCase)))
            {
                argIndex = 1;
            }

            for (int i = argIndex; i < args.Length; i++)
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
                    case "--models" when i + 1 < args.Length:
                        modelsDirectory = args[++i];
                        break;
                    case "--config" when i + 1 < args.Length:
                        configPath = args[++i];
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

            if (string.IsNullOrWhiteSpace(inputPath)
                || string.IsNullOrWhiteSpace(outputDir)
                || string.IsNullOrWhiteSpace(modelsDirectory))
            {
                Console.Error.WriteLine("localize-replay requires --input <rt_windows.ndjson>, --output <dir>, and --models <dir>.");
                PrintUsage();
                return 1;
            }

            var options = new LocalizationReplayOptions
            {
                InputPath = inputPath,
                OutputDirectory = outputDir,
                ModelsDirectory = modelsDirectory,
                ConfigPath = configPath,
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
                Console.Error.WriteLine("localize-replay requires --input <rt_windows.ndjson>.");
                return 1;
            }

            if (!File.Exists(options.InputPath))
            {
                Console.Error.WriteLine($"Input file not found: {options.InputPath}");
                return 1;
            }

            if (string.IsNullOrWhiteSpace(options.OutputDirectory))
            {
                Console.Error.WriteLine("localize-replay requires --output <dir>.");
                return 1;
            }

            if (string.IsNullOrWhiteSpace(options.ModelsDirectory) && dependencies is null)
            {
                Console.Error.WriteLine("localize-replay requires --models <dir>.");
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
                resolvedDependencies = dependencies ?? LoadDependencies(options.ConfigPath, options.ModelsDirectory);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Localization artifacts missing or invalid: {ex.Message}");
                return 1;
            }
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

            int windowsProcessed = 0;
            int requestsIssued = 0;
            int resultsPublished = 0;
            int requestsRefused = 0;
            int pendingRequests = 0;
            bool workerFaulted = false;
            Exception? workerException = null;
            DateTimeOffset? currentWindowEndUtc = null;
            string? currentRtState = null;
            int currentWindowSeq = -1;
            int eventIndex = 0;
            int requestCounter = 0;

            var lastRequestByEpisode = new Dictionary<Guid, RequestContext>();
            var requestsBySequence = new Dictionary<long, RequestContext>();
            var requestsById = new Dictionary<string, RequestContext>(StringComparer.Ordinal);
            var pendingRequestIds = new HashSet<string>(StringComparer.Ordinal);
            var workerResults = new Queue<WorkerResult>();
            var workerSignal = new AutoResetEvent(false);

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
                    var context = BuildRequestContext(runId, request, currentWindowSeq, currentWindowEndUtc, currentRtState, requestCounter);
                    requestCounter++;
                    requestsIssued++;

                    lock (lastRequestByEpisode)
                    {
                        lastRequestByEpisode[request.EpisodeId] = context;
                        requestsBySequence[request.Sequence] = context;
                        requestsById[context.RequestId] = context;
                        pendingRequestIds.Add(context.RequestId);
                        pendingRequests = pendingRequestIds.Count;
                    }

                    WriteEvent(writer, ref eventIndex, BuildRequestEvent(runId, context, resolvedDependencies.EpisodePolicyConfig));
                    limiter.HandleRequest(request);
                };

                worker.OnResult += (request, prediction) =>
                {
                    lock (workerResults)
                    {
                        workerResults.Enqueue(new WorkerResult(request, prediction));
                    }
                    workerSignal.Set();
                };

                worker.OnWorkerFaulted += ex =>
                {
                    workerFaulted = true;
                    workerException = ex;
                };

                policy.NowProvider = () => currentWindowEndUtc ?? DateTimeOffset.MinValue;

                policy.OnDecisionRecord += record =>
                {
                    if (record.DecisionKind == LocalizationDecisionKind.Publish)
                    {
                        var context = ResolveRequestContext(record.EpisodeId, lastRequestByEpisode);
                        if (context is not null)
                        {
                            WriteEvent(writer, ref eventIndex, BuildResultEvent(runId, context, record));
                            RemovePendingRequest(context, pendingRequestIds, ref pendingRequests);
                            resultsPublished++;
                        }
                        return;
                    }

                    if (record.DecisionKind == LocalizationDecisionKind.Refuse)
                    {
                        var context = ResolveRequestContext(record.EpisodeId, lastRequestByEpisode);
                        if (context is not null)
                        {
                            WriteEvent(writer, ref eventIndex, BuildRefusalEvent(runId, context, record));
                            RemovePendingRequest(context, pendingRequestIds, ref pendingRequests);
                            requestsRefused++;
                        }
                        return;
                    }

                    if (record.DecisionKind == LocalizationDecisionKind.Defer
                        && record.ReasonCode == LocalizationDecisionReasonCode.Deferred_Backpressure)
                    {
                        var context = ResolveRequestContext(record.EpisodeId, lastRequestByEpisode);
                        if (context is not null)
                        {
                            WriteEvent(writer, ref eventIndex, BuildRefusalEvent(runId, context, record));
                            RemovePendingRequest(context, pendingRequestIds, ref pendingRequests);
                            requestsRefused++;
                        }
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
                        currentWindowSeq++;
                        currentWindowEndUtc = window.WindowEndUtc;
                        currentRtState = window.RtState;

                        policy.AddWindow(window);
                        DrainWorkerResults(policy, workerResults, pendingRequestIds, ref pendingRequests, requestsBySequence);

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
                    bool drained = WaitForPendingRequests(ref pendingRequests, workerFaulted, workerResults, workerSignal, policy, pendingRequestIds, requestsById, writer, ref eventIndex, runId, requestsBySequence, ref requestsRefused);
                    cts.Cancel();
                    AwaitWorker(workerTask);
                    if (!drained)
                    {
                        Console.Error.WriteLine("Localization replay ended with undrained ML requests.");
                        return 1;
                    }
                }
            }

            string summaryPath = Path.Combine(options.OutputDirectory, "localization_replay_summary.json");
            string eventsHash = ComputeFileHash(eventsPath);
            WriteSummary(summaryPath, runId, windowsProcessed, eventIndex, requestsIssued, requestsRefused, resultsPublished, pendingRequests, eventsHash);

            Console.WriteLine($"windows_processed={windowsProcessed}, requests={requestsIssued}, refused={requestsRefused}, results={resultsPublished}");
            return workerFaulted ? 1 : 0;
        }

        private static LocalizationReplayDependencies LoadDependencies(string? configPath, string? modelsDirectory)
        {
            string resolvedConfigPath = string.IsNullOrWhiteSpace(configPath)
                ? Path.Combine(Directory.GetCurrentDirectory(), "localization_runtime_config.json")
                : configPath;
            string baseDirectory = Path.GetDirectoryName(resolvedConfigPath) ?? Directory.GetCurrentDirectory();
            var runtimeConfig = LocalizationRuntimeConfig.Load(resolvedConfigPath);
            if (!string.IsNullOrWhiteSpace(modelsDirectory))
            {
                runtimeConfig = runtimeConfig with { ArtifactsDirectory = modelsDirectory };
            }
            var validation = runtimeConfig.Validate(baseDirectory);
            if (!validation.IsValid)
            {
                throw new InvalidOperationException(string.Join("; ", validation.Errors));
            }

            string artifactsDirectory = validation.ResolvedArtifactsDirectory ?? runtimeConfig.ArtifactsDirectory;
            var artifacts = LocalizationArtifactsLoader.Load(artifactsDirectory);

            TriggerPolicyThresholds thresholds = artifacts.Thresholds;
            LocalizationEpisodePolicyConfig config = artifacts.PolicyConfig;

            var worker = new LocalizationWorker(artifacts.Pipeline, runtimeConfig.MaxMlQueueDepth);
            return new LocalizationReplayDependencies
            {
                Worker = worker,
                EpisodePolicyConfig = config,
                Thresholds = thresholds,
                RuntimeConfig = runtimeConfig
            };
        }

        private static RequestContext BuildRequestContext(
            string runId,
            RtLocalizationRequest request,
            int windowSeq,
            DateTimeOffset? windowEndUtc,
            string? rtState,
            int requestCounter)
        {
            var counts = new int[request.Row.Channels.Count];
            double totalCounts = 0;
            for (int i = 0; i < request.Row.Channels.Count; i++)
            {
                var value = (int)Math.Round(request.Row.Channels[i]);
                counts[i] = value;
                totalCounts += value;
            }

            DateTimeOffset resolvedWindowEndUtc = request.Row.WindowEndUtc ?? windowEndUtc ?? DateTimeOffset.MinValue;
            string requestId = $"{runId}:{windowSeq}:{requestCounter}";
            return new RequestContext
            {
                RequestId = requestId,
                EpisodeId = request.EpisodeId,
                WindowSeq = windowSeq,
                WindowEndUtc = resolvedWindowEndUtc,
                DurationSeconds = request.Row.DurationSeconds,
                ChannelCounts = counts,
                RtState = rtState ?? string.Empty,
                IsManual = request.IsManual,
                IsProbe = request.IsProbe,
                Sequence = request.Sequence,
                TotalCounts = totalCounts
            };
        }

        private static RequestContext? ResolveRequestContext(Guid episodeId, Dictionary<Guid, RequestContext> lastRequestByEpisode)
        {
            lock (lastRequestByEpisode)
            {
                if (lastRequestByEpisode.TryGetValue(episodeId, out var context))
                {
                    return context;
                }
            }

            return null;
        }

        private static RequestContext? ResolveRequestContextBySequence(long sequence, Dictionary<long, RequestContext> requestsBySequence)
        {
            lock (requestsBySequence)
            {
                if (requestsBySequence.TryGetValue(sequence, out var context))
                {
                    return context;
                }
            }

            return null;
        }

        private static void RemovePendingRequest(RequestContext context, HashSet<string> pendingRequestIds, ref int pendingRequests)
        {
            if (pendingRequestIds.Remove(context.RequestId))
            {
                pendingRequests = pendingRequestIds.Count;
            }
        }

        private static void DrainWorkerResults(
            LocalizationEpisodePolicy policy,
            Queue<WorkerResult> workerResults,
            HashSet<string> pendingRequestIds,
            ref int pendingRequests,
            Dictionary<long, RequestContext> requestsBySequence)
        {
            while (true)
            {
                WorkerResult? result = null;
                lock (workerResults)
                {
                    if (workerResults.Count > 0)
                    {
                        result = workerResults.Dequeue();
                    }
                }

                if (result is null)
                {
                    break;
                }

                policy.OnMlResult(result.Request, result.Prediction);

                var context = ResolveRequestContextBySequence(result.Request.Sequence, requestsBySequence);
                if (context is not null)
                {
                    RemovePendingRequest(context, pendingRequestIds, ref pendingRequests);
                }
            }
        }

        private static bool WaitForPendingRequests(
            ref int pendingRequests,
            bool workerFaulted,
            Queue<WorkerResult> workerResults,
            AutoResetEvent workerSignal,
            LocalizationEpisodePolicy policy,
            HashSet<string> pendingRequestIds,
            Dictionary<string, RequestContext> requestsById,
            StreamWriter writer,
            ref int eventIndex,
            string runId,
            Dictionary<long, RequestContext> requestsBySequence,
            ref int requestsRefused)
        {
            if (workerFaulted)
            {
                var snapshot = pendingRequestIds.ToArray();
                foreach (var requestId in snapshot)
                {
                    if (!requestsById.TryGetValue(requestId, out var context))
                    {
                        continue;
                    }

                    WriteEvent(writer, ref eventIndex, BuildRefusalEvent(runId, context, new[] { "WORKER_FAULTED" }));
                    RemovePendingRequest(context, pendingRequestIds, ref pendingRequests);
                    requestsRefused++;
                }
                return false;
            }

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            while (pendingRequests > 0)
            {
                DrainWorkerResults(policy, workerResults, pendingRequestIds, ref pendingRequests, requestsBySequence);
                if (pendingRequests == 0)
                {
                    return true;
                }

                if (stopwatch.Elapsed > TimeSpan.FromSeconds(10))
                {
                    break;
                }

                workerSignal.WaitOne(TimeSpan.FromMilliseconds(50));
            }

            if (pendingRequests > 0)
            {
                var snapshot = pendingRequestIds.ToArray();
                foreach (var requestId in snapshot)
                {
                    if (!requestsById.TryGetValue(requestId, out var context))
                    {
                        continue;
                    }

                    WriteEvent(writer, ref eventIndex, BuildRefusalEvent(runId, context, new[] { "PENDING_REQUESTS_NOT_DRAINED" }));
                    RemovePendingRequest(context, pendingRequestIds, ref pendingRequests);
                    requestsRefused++;
                }
            }

            return pendingRequests == 0;
        }

        private static LocalizationEvent BuildRequestEvent(string runId, RequestContext context, LocalizationEpisodePolicyConfig config)
        {
            return new LocalizationEvent
            {
                EventType = "localization_requested",
                RunId = runId,
                WindowEndUtc = context.WindowEndUtc,
                WindowSeq = context.WindowSeq,
                RequestId = context.RequestId,
                DurationSeconds = context.DurationSeconds,
                ChannelCounts = context.ChannelCounts,
                RtState = context.RtState,
                TriggerBasis = BuildTriggerBasis(context, config)
            };
        }

        private static LocalizationEvent BuildRefusalEvent(string runId, RequestContext context, LocalizationDecisionRecord record)
        {
            return new LocalizationEvent
            {
                EventType = "localization_refused",
                RunId = runId,
                WindowEndUtc = context.WindowEndUtc,
                WindowSeq = context.WindowSeq,
                RequestId = context.RequestId,
                DurationSeconds = context.DurationSeconds,
                ChannelCounts = context.ChannelCounts,
                RtState = context.RtState,
                RefusalReasonCodes = MapRefusalReasonCodes(record)
            };
        }

        private static LocalizationEvent BuildRefusalEvent(string runId, RequestContext context, IReadOnlyList<string> reasonCodes)
        {
            return new LocalizationEvent
            {
                EventType = "localization_refused",
                RunId = runId,
                WindowEndUtc = context.WindowEndUtc,
                WindowSeq = context.WindowSeq,
                RequestId = context.RequestId,
                DurationSeconds = context.DurationSeconds,
                ChannelCounts = context.ChannelCounts,
                RtState = context.RtState,
                RefusalReasonCodes = reasonCodes.ToArray()
            };
        }

        private static LocalizationEvent BuildResultEvent(string runId, RequestContext context, LocalizationDecisionRecord record)
        {
            var coords = record.Result.PublishedCoords ?? Array.Empty<double>();
            return new LocalizationEvent
            {
                EventType = "localization_result",
                RunId = runId,
                WindowEndUtc = context.WindowEndUtc,
                WindowSeq = context.WindowSeq,
                RequestId = context.RequestId,
                Label = record.MlContext?.OutcomeLabel ?? "Unknown",
                CoordsCm = coords,
                Confidence = BuildConfidence(record),
                Ood = BuildOod(record),
                ModelId = record.MlContext?.ModelId
            };
        }

        private static TriggerBasis BuildTriggerBasis(RequestContext context, LocalizationEpisodePolicyConfig config)
        {
            return new TriggerBasis
            {
                RequestKind = context.IsProbe ? "probe" : "final",
                IsManual = context.IsManual,
                AccumulatedDurationSeconds = context.DurationSeconds,
                AccumulatedTotalCounts = context.TotalCounts,
                CheckEveryCounts = config.CheckEveryCounts,
                MinDurationSeconds = config.MinPublishDurationSeconds,
                MaxDurationSeconds = config.MaxPublishDurationSeconds
            };
        }

        private static LocalizationConfidence? BuildConfidence(LocalizationDecisionRecord record)
        {
            if (record.MlContext is null || !double.IsFinite(record.MlContext.ClassifierProbability))
            {
                return null;
            }

            return new LocalizationConfidence
            {
                ClassifierProbability = record.MlContext.ClassifierProbability,
                InferenceTimeMs = record.MlContext.InferenceTimeMs
            };
        }

        private static LocalizationOod? BuildOod(LocalizationDecisionRecord record)
        {
            if (record.MlContext is null || !double.IsFinite(record.MlContext.MahalanobisDistance))
            {
                return null;
            }

            return new LocalizationOod
            {
                IsOod = record.MlContext.IsOutOfDistribution,
                Distance = record.MlContext.MahalanobisDistance
            };
        }

        private static string[] MapRefusalReasonCodes(LocalizationDecisionRecord record)
        {
            return record.ReasonCode switch
            {
                LocalizationDecisionReasonCode.Refused_OOD => new[] { "OOD" },
                LocalizationDecisionReasonCode.Refused_OODAtMaxDuration => new[] { "OOD_AT_MAX_DURATION" },
                LocalizationDecisionReasonCode.Refused_InvalidPredictionShape => new[] { "INVALID_PREDICTION_SHAPE" },
                LocalizationDecisionReasonCode.Refused_MlTimeout => new[] { "ML_TIMEOUT" },
                LocalizationDecisionReasonCode.Refused_Cancelled => new[] { "CANCELLED" },
                LocalizationDecisionReasonCode.Refused_ArtifactsNotLoaded => new[] { "MODEL_NOT_LOADED" },
                LocalizationDecisionReasonCode.Refused_MlException => new[] { "ML_EXCEPTION" },
                LocalizationDecisionReasonCode.Refused_RuntimeMisconfigured => new[] { "RUNTIME_MISCONFIGURED" },
                LocalizationDecisionReasonCode.Refused_QueueSaturated => new[] { "QUEUE_SATURATED" },
                LocalizationDecisionReasonCode.Deferred_Backpressure => new[] { "BACKPRESSURE_DEFERRED" },
                _ => new[] { record.ReasonCode.ToString() }
            };
        }

        private static void WriteEvent(StreamWriter writer, ref int eventIndex, LocalizationEvent evt)
        {
            var buffer = new ArrayBufferWriter<byte>();
            using (var jsonWriter = new Utf8JsonWriter(buffer))
            {
                jsonWriter.WriteStartObject();
                jsonWriter.WriteString("schema_version", EventSchemaVersion);
                jsonWriter.WriteString("run_id", evt.RunId);
                jsonWriter.WriteNumber("event_index", eventIndex);
                jsonWriter.WriteString("window_end_utc", evt.WindowEndUtc.ToString("O", CultureInfo.InvariantCulture));
                jsonWriter.WriteNumber("window_seq", evt.WindowSeq);
                jsonWriter.WriteString("event_type", evt.EventType);

                if (!string.IsNullOrWhiteSpace(evt.RequestId))
                {
                    jsonWriter.WriteString("request_id", evt.RequestId);
                }

                if (evt.DurationSeconds.HasValue && double.IsFinite(evt.DurationSeconds.Value))
                {
                    WriteNumber(jsonWriter, "duration_s", evt.DurationSeconds.Value);
                }

                if (evt.ChannelCounts is not null)
                {
                    WriteIntArray(jsonWriter, "channel_counts", evt.ChannelCounts);
                }

                if (!string.IsNullOrWhiteSpace(evt.RtState))
                {
                    jsonWriter.WriteString("rt_state", evt.RtState);
                }

                if (evt.TriggerBasis is not null)
                {
                    jsonWriter.WritePropertyName("trigger_basis");
                    jsonWriter.WriteStartObject();
                    jsonWriter.WriteString("request_kind", evt.TriggerBasis.RequestKind);
                    jsonWriter.WriteBoolean("is_manual", evt.TriggerBasis.IsManual);
                    WriteNumber(jsonWriter, "accumulated_duration_s", evt.TriggerBasis.AccumulatedDurationSeconds);
                    WriteNumber(jsonWriter, "accumulated_counts_total", evt.TriggerBasis.AccumulatedTotalCounts);
                    jsonWriter.WriteNumber("check_every_counts", evt.TriggerBasis.CheckEveryCounts);
                    WriteNumber(jsonWriter, "min_duration_s", evt.TriggerBasis.MinDurationSeconds);
                    WriteNumber(jsonWriter, "max_duration_s", evt.TriggerBasis.MaxDurationSeconds);
                    jsonWriter.WriteEndObject();
                }

                if (evt.RefusalReasonCodes is not null)
                {
                    WriteStringArray(jsonWriter, "refusal_reason_codes", evt.RefusalReasonCodes);
                }

                if (!string.IsNullOrWhiteSpace(evt.Label))
                {
                    jsonWriter.WriteString("label", evt.Label);
                }

                if (evt.CoordsCm is not null)
                {
                    WriteDoubleArray(jsonWriter, "coords_cm", evt.CoordsCm);
                }

                if (evt.Confidence is not null)
                {
                    jsonWriter.WritePropertyName("confidence");
                    jsonWriter.WriteStartObject();
                    WriteNumber(jsonWriter, "classifier_probability", evt.Confidence.ClassifierProbability);
                    if (evt.Confidence.InferenceTimeMs.HasValue && double.IsFinite(evt.Confidence.InferenceTimeMs.Value))
                    {
                        WriteNumber(jsonWriter, "inference_time_ms", evt.Confidence.InferenceTimeMs.Value);
                    }
                    jsonWriter.WriteEndObject();
                }

                if (!string.IsNullOrWhiteSpace(evt.ModelId))
                {
                    jsonWriter.WriteString("model_id", evt.ModelId);
                }

                if (evt.Ood is not null)
                {
                    jsonWriter.WritePropertyName("ood");
                    jsonWriter.WriteStartObject();
                    jsonWriter.WriteBoolean("is_ood", evt.Ood.IsOod);
                    WriteNumber(jsonWriter, "distance", evt.Ood.Distance);
                    if (evt.Ood.Threshold.HasValue && double.IsFinite(evt.Ood.Threshold.Value))
                    {
                        WriteNumber(jsonWriter, "threshold", evt.Ood.Threshold.Value);
                    }
                    jsonWriter.WriteEndObject();
                }

                jsonWriter.WriteEndObject();
            }

            writer.WriteLine(Encoding.UTF8.GetString(buffer.WrittenSpan));
            writer.Flush();
            eventIndex++;
        }

        private static void WriteSummary(
            string summaryPath,
            string runId,
            int windowsProcessed,
            int eventsWritten,
            int requestsIssued,
            int requestsRefused,
            int resultsPublished,
            int pendingRequests,
            string eventsHash)
        {
            var buffer = new ArrayBufferWriter<byte>();
            using (var jsonWriter = new Utf8JsonWriter(buffer))
            {
                jsonWriter.WriteStartObject();
                jsonWriter.WriteString("schema_version", SummarySchemaVersion);
                jsonWriter.WriteString("run_id", runId);
                jsonWriter.WriteNumber("windows_processed", windowsProcessed);
                jsonWriter.WriteNumber("events_written", eventsWritten);
                jsonWriter.WriteNumber("requests", requestsIssued);
                jsonWriter.WriteNumber("refusals", requestsRefused);
                jsonWriter.WriteNumber("results", resultsPublished);
                jsonWriter.WriteNumber("pending_requests", pendingRequests);
                jsonWriter.WriteString("events_sha256", eventsHash);
                jsonWriter.WriteEndObject();
            }

            File.WriteAllText(summaryPath, Encoding.UTF8.GetString(buffer.WrittenSpan));
        }

        private static string ComputeFileHash(string path)
        {
            using var sha = SHA256.Create();
            using var stream = File.OpenRead(path);
            var hash = sha.ComputeHash(stream);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }

        private static void WriteNumber(Utf8JsonWriter writer, string name, double value)
        {
            writer.WritePropertyName(name);
            writer.WriteRawValue(value.ToString("G17", CultureInfo.InvariantCulture), skipInputValidation: true);
        }

        private static void WriteIntArray(Utf8JsonWriter writer, string name, IReadOnlyList<int> values)
        {
            writer.WritePropertyName(name);
            writer.WriteStartArray();
            foreach (var value in values)
            {
                writer.WriteNumberValue(value);
            }
            writer.WriteEndArray();
        }

        private static void WriteDoubleArray(Utf8JsonWriter writer, string name, IReadOnlyList<double> values)
        {
            writer.WritePropertyName(name);
            writer.WriteStartArray();
            foreach (var value in values)
            {
                writer.WriteRawValue(value.ToString("G17", CultureInfo.InvariantCulture), skipInputValidation: true);
            }
            writer.WriteEndArray();
        }

        private static void WriteStringArray(Utf8JsonWriter writer, string name, IReadOnlyList<string> values)
        {
            writer.WritePropertyName(name);
            writer.WriteStartArray();
            foreach (var value in values)
            {
                writer.WriteStringValue(value);
            }
            writer.WriteEndArray();
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
            Console.WriteLine("Usage: Listen-N localize-replay --input <rt_windows.ndjson> --output <dir> --models <dir> [--config <path>] [--run-id <id>]");
            Console.WriteLine("       Listen-N localize --replay --input <rt_windows.ndjson> --output <dir> --models <dir> [--config <path>] [--run-id <id>]");
        }

        private sealed record RequestContext
        {
            public required string RequestId { get; init; }
            public required Guid EpisodeId { get; init; }
            public required int WindowSeq { get; init; }
            public required DateTimeOffset WindowEndUtc { get; init; }
            public required double DurationSeconds { get; init; }
            public required int[] ChannelCounts { get; init; }
            public required string RtState { get; init; }
            public required bool IsManual { get; init; }
            public required bool IsProbe { get; init; }
            public required long Sequence { get; init; }
            public required double TotalCounts { get; init; }
        }

        private sealed record WorkerResult(RtLocalizationRequest Request, LocalizationPrediction Prediction);

        private sealed record LocalizationEvent
        {
            public required string EventType { get; init; }
            public required string RunId { get; init; }
            public required DateTimeOffset WindowEndUtc { get; init; }
            public required int WindowSeq { get; init; }
            public string? RequestId { get; init; }
            public double? DurationSeconds { get; init; }
            public int[]? ChannelCounts { get; init; }
            public string? RtState { get; init; }
            public TriggerBasis? TriggerBasis { get; init; }
            public string[]? RefusalReasonCodes { get; init; }
            public string? Label { get; init; }
            public double[]? CoordsCm { get; init; }
            public LocalizationConfidence? Confidence { get; init; }
            public LocalizationOod? Ood { get; init; }
            public string? ModelId { get; init; }
        }

        private sealed record TriggerBasis
        {
            public required string RequestKind { get; init; }
            public required bool IsManual { get; init; }
            public required double AccumulatedDurationSeconds { get; init; }
            public required double AccumulatedTotalCounts { get; init; }
            public required int CheckEveryCounts { get; init; }
            public required double MinDurationSeconds { get; init; }
            public required double MaxDurationSeconds { get; init; }
        }

        private sealed record LocalizationConfidence
        {
            public required double ClassifierProbability { get; init; }
            public double? InferenceTimeMs { get; init; }
        }

        private sealed record LocalizationOod
        {
            public required bool IsOod { get; init; }
            public required double Distance { get; init; }
            public double? Threshold { get; init; }
        }

    }
}
