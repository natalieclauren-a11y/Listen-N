using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Integrated.Runtime;
using Xunit;
using RtLocalizationRequest = Integrated.Runtime.LocalizationRequest;

namespace Listen_N.Tests
{
    public sealed class LocalizationReplayRunnerTests
    {
        [Fact]
        public void LocalizeReplay_WritesTriggerEvents()
        {
            using var tempDir = Directory.CreateTempSubdirectory();
            string inputPath = Path.Combine(tempDir.FullName, "rt_windows.ndjson");
            string outputDir = Path.Combine(tempDir.FullName, "out");
            Directory.CreateDirectory(outputDir);

            var start = new DateTimeOffset(2024, 01, 01, 0, 0, 0, TimeSpan.Zero);
            var counts = Enumerable.Repeat(5.0, 15).ToArray();
            using (var writer = new StreamWriter(inputPath))
            {
                for (int i = 0; i < 6; i++)
                {
                    var windowStart = start.AddSeconds(i);
                    var windowEnd = start.AddSeconds(i + 1);
                    var record = new
                    {
                        window_start_utc = windowStart.ToString("O"),
                        window_end_utc = windowEnd.ToString("O"),
                        duration_seconds = 1.0,
                        counts15 = counts,
                        rt_state = "Nominal",
                        rate_total_cps = 75.0,
                        quality_scalar = 5.0
                    };
                    writer.WriteLine(JsonSerializer.Serialize(record));
                }
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
                EpisodePolicyConfig = new LocalizationEpisodePolicyConfig(),
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
            string contents = File.ReadAllText(eventsPath);
            Assert.Contains("\"event_type\":\"trigger_evaluated\"", contents);
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
    }
}
