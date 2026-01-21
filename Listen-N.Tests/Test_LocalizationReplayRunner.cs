using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using Integrated.Runtime;
using Localization.ML;
using Microsoft.ML;
using Xunit;
using RtLocalizationRequest = Integrated.Runtime.LocalizationRequest;

namespace Listen_N.Tests
{
    public sealed class LocalizationReplayRunnerTests
    {
        [Fact]
        public void LocalizeReplay_WritesLocalizationEvents()
        {
            var tempDir = Directory.CreateTempSubdirectory();
            try
            {
                string inputPath = Path.Combine(tempDir.FullName, "rt_windows.ndjson");
                string outputDir = Path.Combine(tempDir.FullName, "out");
                Directory.CreateDirectory(outputDir);

                var start = new DateTimeOffset(2024, 01, 01, 0, 0, 0, TimeSpan.Zero);
                var counts = Enumerable.Repeat(50.0, 15).ToArray();
                using (var writer = new StreamWriter(inputPath))
                {
                    var windowStart = start;
                    var windowEnd = start.AddSeconds(1);
                    var record = new
                    {
                        window_start_utc = windowStart.ToString("O"),
                        window_end_utc = windowEnd.ToString("O"),
                        duration_seconds = 1.0,
                        counts15 = counts,
                        rt_state = "Hold",
                        rate_total_cps = 750.0,
                        quality_scalar = 5.0
                    };
                    writer.WriteLine(JsonSerializer.Serialize(record));
                }

                var runtimeConfig = new LocalizationRuntimeConfig
                {
                    ArtifactsDirectory = "unused",
                    TriggerPolicyPath = "unused",
                    AllowedSchemaHashes = new[] { "v1" },
                    SupportedTrainingDurationsSec = new[] { 30, 60 },
                    MaxMlQueueDepth = 1,
                    MaxMlRequestsPerEpisode = 2,
                    DecisionLoggingEnabled = false,
                    FailFastOnStartupError = true
                };

                var worker = new LocalizationWorker(new StubLocalizer(), capacity: 1);
                var dependencies = new LocalizationReplayDependencies
                {
                    Worker = worker,
                    EpisodePolicyConfig = new LocalizationEpisodePolicyConfig
                    {
                        ConfuseDebounceWindows = 1,
                        RecoverDebounceWindows = 1,
                        MinPublishDurationSeconds = 1.0,
                        MaxPublishDurationSeconds = 1.0,
                        CheckEveryCounts = 1
                    },
                    Thresholds = TriggerPolicyThresholds.Defaults,
                    RuntimeConfig = runtimeConfig
                };

                var options = new LocalizationReplayOptions
                {
                    InputPath = inputPath,
                    OutputDirectory = outputDir,
                    RunId = "test-run"
                };

                int result = LocalizationReplayRunner.Run(options, dependencies);
                Assert.Equal(0, result);

                string eventsPath = Path.Combine(outputDir, "localization_events.ndjson");
                Assert.True(File.Exists(eventsPath));
                string[] lines = File.ReadAllLines(eventsPath);
                Assert.NotEmpty(lines);

                var eventTypes = lines.Select(line =>
                {
                    using var document = JsonDocument.Parse(line);
                    return document.RootElement.GetProperty("event_type").GetString();
                }).ToList();

                Assert.Contains("localization_requested", eventTypes);
                Assert.Contains("localization_result", eventTypes);

                for (int i = 0; i < lines.Length; i++)
                {
                    using var document = JsonDocument.Parse(lines[i]);
                    Assert.Equal("loc_events.v1", document.RootElement.GetProperty("schema_version").GetString());
                    Assert.Equal(i, document.RootElement.GetProperty("event_index").GetInt32());
                }
            }
            finally
            {
                if (tempDir.Exists)
                {
                    tempDir.Delete(recursive: true);
                }
            }
        }

        [Fact]
        public void LocalizeReplay_FailsWhenPipelineConfigMissing()
        {
            var tempDir = Directory.CreateTempSubdirectory();
            var originalError = Console.Error;
            try
            {
                string inputPath = Path.Combine(tempDir.FullName, "rt_windows.ndjson");
                string outputDir = Path.Combine(tempDir.FullName, "out");
                string modelsDir = Path.Combine(tempDir.FullName, "models");
                string configPath = Path.Combine(tempDir.FullName, "localization_runtime_config.json");
                string triggerPolicyPath = Path.Combine(tempDir.FullName, "trigger_policy.json");
                Directory.CreateDirectory(outputDir);
                Directory.CreateDirectory(modelsDir);

                File.WriteAllText(inputPath, BuildSingleWindowInput());
                WriteTriggerPolicy(triggerPolicyPath);
                WriteRuntimeConfig(configPath, modelsDir, triggerPolicyPath);

                using var errorWriter = new StringWriter();
                Console.SetError(errorWriter);

                var options = new LocalizationReplayOptions
                {
                    InputPath = inputPath,
                    OutputDirectory = outputDir,
                    ModelsDirectory = modelsDir,
                    ConfigPath = configPath,
                    RunId = "test-run"
                };

                int result = LocalizationReplayRunner.Run(options, dependencies: null);
                Assert.Equal(1, result);
                Assert.Contains("pipeline_config.json", errorWriter.ToString());
            }
            finally
            {
                Console.SetError(originalError);
                if (tempDir.Exists)
                {
                    tempDir.Delete(recursive: true);
                }
            }
        }

        [Fact]
        public void LocalizeReplay_LoadsPipelineBundle()
        {
            var tempDir = Directory.CreateTempSubdirectory();
            try
            {
                string inputPath = Path.Combine(tempDir.FullName, "rt_windows.ndjson");
                string outputDir = Path.Combine(tempDir.FullName, "out");
                string modelsDir = Path.Combine(tempDir.FullName, "models");
                string configPath = Path.Combine(tempDir.FullName, "localization_runtime_config.json");
                string triggerPolicyPath = Path.Combine(tempDir.FullName, "trigger_policy.json");
                Directory.CreateDirectory(outputDir);
                Directory.CreateDirectory(modelsDir);

                File.WriteAllText(inputPath, BuildSingleWindowInput());
                WriteTriggerPolicy(triggerPolicyPath);
                WriteRuntimeConfig(configPath, modelsDir, triggerPolicyPath);
                WritePipelineBundle(modelsDir);

                var options = new LocalizationReplayOptions
                {
                    InputPath = inputPath,
                    OutputDirectory = outputDir,
                    ModelsDirectory = modelsDir,
                    ConfigPath = configPath,
                    RunId = "test-run"
                };

                int result = LocalizationReplayRunner.Run(options, dependencies: null);
                Assert.Equal(0, result);

                string eventsPath = Path.Combine(outputDir, "localization_events.ndjson");
                Assert.True(File.Exists(eventsPath));
            }
            finally
            {
                if (tempDir.Exists)
                {
                    tempDir.Delete(recursive: true);
                }
            }
        }

        private sealed class StubLocalizer : ILocalizer
        {
            public LocalizationPrediction Predict(RtLocalizationRequest request, System.Threading.CancellationToken cancellationToken)
            {
                return new LocalizationPrediction
                {
                    IsOutOfDistribution = false,
                    MahalanobisDistance = 0,
                    ClassifierProbability = 0.99,
                    Label = "Single",
                    PredictedVector = new[] { 1.0, 2.0, 3.0 },
                    ModelId = "stub"
                };
            }
        }

        private static string BuildSingleWindowInput()
        {
            var start = new DateTimeOffset(2024, 01, 01, 0, 0, 0, TimeSpan.Zero);
            var counts = Enumerable.Repeat(50.0, 15).ToArray();
            var record = new
            {
                window_start_utc = start.ToString("O"),
                window_end_utc = start.AddSeconds(1).ToString("O"),
                duration_seconds = 1.0,
                counts15 = counts,
                rt_state = "Hold",
                rate_total_cps = 750.0,
                quality_scalar = 5.0
            };

            return JsonSerializer.Serialize(record);
        }

        private static void WriteRuntimeConfig(string configPath, string artifactsDirectory, string triggerPolicyPath)
        {
            var runtimeConfig = new LocalizationRuntimeConfig
            {
                ArtifactsDirectory = artifactsDirectory,
                TriggerPolicyPath = triggerPolicyPath,
                AllowedSchemaHashes = new[] { "v1" },
                SupportedTrainingDurationsSec = new[] { 30, 60 },
                MaxMlQueueDepth = 1,
                MaxMlRequestsPerEpisode = 2,
                DecisionLoggingEnabled = false,
                FailFastOnStartupError = true
            };

            var json = JsonSerializer.Serialize(runtimeConfig, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(configPath, json);
        }

        private static void WriteTriggerPolicy(string policyPath)
        {
            var doc = new TriggerPolicyDocument
            {
                Nmin_15cm_30s = 1,
                Nmin_15cm_60s = 1,
                ConfuseDebounceWindows = 1,
                RecoverDebounceWindows = 1,
                QualityMin = 0.1,
                CheckEveryCounts = 1,
                MinPublishDurationSeconds = 1.0,
                MaxPublishDurationSeconds = 1.0,
                ThrashWindowCount = 1,
                ThrashChangeThreshold = 1,
                StabilityToleranceCm = 1.0,
                StabilityK = 1,
                EarlyStopProbability = 0.5,
                EarlyStopK = 1,
                PublishProbabilityMin = 0.5
            };

            var json = JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(policyPath, json);
        }

        private static void WritePipelineBundle(string artifactsDirectory)
        {
            var mlContext = new MLContext(seed: 1);
            var featureBuilder = new FeatureBuilder();
            var classifierData = mlContext.Data.LoadFromEnumerable(new[]
            {
                new ClassificationExample { Label = true, Features = new float[FeatureBuilder.ExpectedFeatureCount] },
                new ClassificationExample { Label = false, Features = Enumerable.Repeat(1f, FeatureBuilder.ExpectedFeatureCount).ToArray() }
            });
            var classifier = mlContext.BinaryClassification.Trainers.SdcaLogisticRegression().Fit(classifierData);

            var singleRegressor = BuildRegressorGroup(mlContext, new[] { "x", "y", "z" });
            var dualRegressor = BuildRegressorGroup(mlContext, new[] { "x1", "y1", "z1", "x2", "y2", "z2" });
            var mahalanobis = new MahalanobisScorer(new double[4], Matrix4x4.Identity);

            var config = new PipelineConfiguration
            {
                Epsilon = featureBuilder.Epsilon,
                OutOfDistributionThreshold = 999.0,
                MinimumSeparationCm = 1.0,
                StrictProbability = 0.5,
                SchemaVersion = SchemaStampBuilder.DefaultSchemaVersion,
                FeatureNames = featureBuilder.FeatureNames,
                FeatureColumns = featureBuilder.FeatureNames,
                DipolePositions = featureBuilder.DipolePositions
            };

            var stamp = new FeatureSchemaStamp
            {
                SchemaVersion = config.SchemaVersion,
                FeatureNames = featureBuilder.FeatureNames,
                FeatureColumns = featureBuilder.FeatureNames,
                Epsilon = featureBuilder.Epsilon,
                ChannelCount = FeatureBuilder.ChannelCount,
                DipolePositions = featureBuilder.DipolePositions
            };
            config.SchemaHash = FeatureSchemaHasher.ComputeHash(stamp);

            var pipeline = new LocalizationPipeline(
                mlContext,
                featureBuilder,
                classifier,
                singleRegressor,
                dualRegressor,
                mahalanobis,
                config);
            pipeline.Save(artifactsDirectory);

            var identity = new PipelineIdentity
            {
                SchemaHash = config.SchemaHash,
                FeatureHash = "feature-hash",
                TrainingDurationSec = 30,
                TrainingDatasetFingerprint = "dataset-fingerprint",
                BuildTimestampUtc = DateTime.UtcNow.ToString("O"),
                CommitHash = "test"
            };

            var identityJson = JsonSerializer.Serialize(identity, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(Path.Combine(artifactsDirectory, "pipeline_identity.json"), identityJson);
        }

        private static RegressionModelGroup BuildRegressorGroup(MLContext mlContext, IEnumerable<string> targets)
        {
            var data = mlContext.Data.LoadFromEnumerable(new[]
            {
                new RegressionExample { Label = 1.0f, Features = new float[FeatureBuilder.ExpectedFeatureCount] },
                new RegressionExample { Label = 2.0f, Features = Enumerable.Repeat(1f, FeatureBuilder.ExpectedFeatureCount).ToArray() }
            });
            var models = new Dictionary<string, ITransformer>();
            foreach (var target in targets)
            {
                var model = mlContext.Regression.Trainers.Sdca().Fit(data);
                models[target] = model;
            }

            return new RegressionModelGroup(mlContext, models);
        }
    }
}
