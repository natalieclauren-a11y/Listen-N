using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Integrated.Contracts;
using Integrated.Runtime;
using Xunit;
using RtLocalizationPrediction = Integrated.Runtime.LocalizationPrediction;
using RtLocalizationRequest = Integrated.Runtime.LocalizationRequest;

namespace Listen_N.Tests
{
    public sealed class LocalizationRuntimeSafetyTests
    {
        [Fact]
        public void MissingArtifactsDirectoryDisablesLocalization()
        {
            var config = new LocalizationRuntimeConfig
            {
                ArtifactsDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")),
                TriggerPolicyPath = "trigger_policy.json",
                AllowedSchemaHashes = new[] { "v1" },
                SupportedTrainingDurationsSec = new[] { 30, 60 },
                MaxMlQueueDepth = 1,
                MaxMlRequestsPerEpisode = 2,
                DecisionLoggingEnabled = true,
                FailFastOnStartupError = true
            };

            var result = config.Validate();
            Assert.False(result.IsValid);
            Assert.Contains(result.Errors, error => error.Contains("Artifacts directory not found", StringComparison.OrdinalIgnoreCase));

            var record = LocalizationDecisionRecordFactory.CreateRuntimeMisconfigured(Guid.NewGuid(), result.Errors.FirstOrDefault());
            Assert.Equal(LocalizationDecisionReasonCode.Refused_RuntimeMisconfigured, record.ReasonCode);
            Assert.Equal(LocalizationDecisionOutcome.Refused, record.Result.Outcome);
        }

        [Fact]
        public void CorruptTriggerPolicyDisablesLocalization()
        {
            using var tempDir = new TempDirectory();
            var manifestPath = Path.Combine(tempDir.Path, "manifest.json");
            File.WriteAllText(manifestPath, @"{
  ""SchemaVersion"": ""1"",
  ""SchemaHash"": ""v1"",
  ""TrainingDurationsSeconds"": [30, 60],
  ""TriggerPolicyPath"": ""trigger_policy.json"",
  ""ModelPaths"": { ""classifier"": ""classifier.zip"", ""single_regressor"": ""single_regressor"", ""dual_regressor"": ""dual_regressor"", ""pipeline_config"": ""pipeline_config.json"" }
}");
            File.WriteAllText(Path.Combine(tempDir.Path, "trigger_policy.json"), "{ invalid json");

            var config = new LocalizationRuntimeConfig
            {
                ArtifactsDirectory = tempDir.Path,
                TriggerPolicyPath = "trigger_policy.json",
                AllowedSchemaHashes = new[] { "v1" },
                SupportedTrainingDurationsSec = new[] { 30, 60 },
                MaxMlQueueDepth = 1,
                MaxMlRequestsPerEpisode = 2,
                DecisionLoggingEnabled = true,
                FailFastOnStartupError = true
            };

            var result = config.Validate();
            Assert.False(result.IsValid);
            Assert.Contains(result.Errors, error => error.Contains("Trigger policy invalid", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public async Task MlExceptionProducesDecisionRecordAndDegradedHealth()
        {
            var policy = new LocalizationEpisodePolicy(new LocalizationEpisodePolicyConfig(), TriggerPolicyThresholds.Defaults)
            {
                RunId = Guid.NewGuid()
            };
            var worker = new LocalizationWorker(new ThrowingLocalizer(), capacity: 1);
            policy.OnRequestMl += worker.Enqueue;
            worker.OnResult += policy.OnMlResult;

            var tcs = new TaskCompletionSource<LocalizationDecisionRecord>(TaskCreationOptions.RunContinuationsAsynchronously);
            policy.OnDecisionRecord += record =>
            {
                if (record.DecisionKind == LocalizationDecisionKind.Refuse)
                {
                    tcs.TrySetResult(record);
                }
            };

            using var cts = new CancellationTokenSource();
            var runTask = worker.Start(cts.Token);
            var request = policy.BuildManualProbeRequest();
            worker.Enqueue(request);

            var completed = await Task.WhenAny(tcs.Task, Task.Delay(2000));
            cts.Cancel();
            await AwaitStopAsync(runTask);

            Assert.Equal(tcs.Task, completed);
            var decision = await tcs.Task;
            Assert.Equal(LocalizationDecisionReasonCode.Refused_MlException, decision.ReasonCode);

            var health = new LocalizationHealthTracker(refusalThreshold: 2, queueSaturationThreshold: 2);
            health.RecordDecision(decision);
            Assert.Equal(LocalizationHealthState.Degraded, health.State);
        }

        [Fact]
        public void RepeatedOodRefusalsDegradeHealth()
        {
            var health = new LocalizationHealthTracker(refusalThreshold: 2, queueSaturationThreshold: 2);
            var record = CreateDecisionRecord(LocalizationDecisionReasonCode.Refused_OOD, LocalizationDecisionOutcome.Refused);

            health.RecordDecision(record);
            Assert.Equal(LocalizationHealthState.Healthy, health.State);
            health.RecordDecision(record);
            Assert.Equal(LocalizationHealthState.Degraded, health.State);
        }

        [Fact]
        public void QueueSaturationEmitsDecisionRecord()
        {
            var policy = new LocalizationEpisodePolicy(new LocalizationEpisodePolicyConfig(), TriggerPolicyThresholds.Defaults)
            {
                RunId = Guid.NewGuid()
            };
            var worker = new LocalizationWorker(new StableLocalizer(), capacity: 1);
            var config = new LocalizationRuntimeConfig
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
            var health = new LocalizationHealthTracker(2, 1);
            var limiter = new LocalizationMlRequestLimiter(worker, policy, config, health);

            LocalizationDecisionRecord? record = null;
            policy.OnDecisionRecord += decision => record = decision;

            var request = policy.BuildManualProbeRequest();
            limiter.HandleRequest(request);
            limiter.HandleRequest(request);

            Assert.NotNull(record);
            Assert.Equal(LocalizationDecisionReasonCode.Refused_QueueSaturated, record?.ReasonCode);
            Assert.Equal(LocalizationDecisionOutcome.Refused, record?.Result.Outcome);
        }

        private static LocalizationDecisionRecord CreateDecisionRecord(LocalizationDecisionReasonCode reason, LocalizationDecisionOutcome outcome)
        {
            return new LocalizationDecisionRecord
            {
                RunId = Guid.NewGuid(),
                TimestampUtc = DateTime.UtcNow,
                DecisionKind = outcome == LocalizationDecisionOutcome.Deferred ? LocalizationDecisionKind.Defer : LocalizationDecisionKind.Refuse,
                ReasonCode = reason,
                EpisodeId = Guid.NewGuid(),
                EpisodeActive = false,
                RtContext = new LocalizationDecisionRtContext
                {
                    WindowDurationSeconds = 0,
                    WindowTotalCounts = 0,
                    RtStateName = string.Empty,
                    ConfusionMetric = double.NaN,
                    IsConfusedCandidate = false,
                    QualityScalar = double.NaN,
                    RateTotalCps = double.NaN
                },
                EpisodeContext = new LocalizationDecisionEpisodeContext
                {
                    AccumulatedDurationSeconds = 0,
                    AccumulatedTotalCounts = 0,
                    ConfusionDebounceCount = 0,
                    RecoveryDebounceCount = 0,
                    StabilityDeltaCm = double.NaN,
                    ConsecutiveStableCount = 0,
                    RequiredStableCount = 0
                },
                PolicyContext = new LocalizationDecisionPolicyContext
                {
                    MinPublishDurationSeconds = 0,
                    MaxPublishDurationSeconds = 0,
                    Nmin15cm_Single = 0,
                    Nmin15cm_Dual = 0,
                    ActiveThresholdUsed = 0,
                    ActiveThresholdMode = string.Empty,
                    CountsGatePassed = false,
                    DurationGatePassed = false,
                    OodGatePassed = false,
                    StabilityGatePassed = false,
                    ProbabilityGatePassed = false,
                    GatingReasons = Array.Empty<string>()
                },
                MlContext = null,
                Result = new LocalizationDecisionResult
                {
                    Outcome = outcome,
                    LowStatistics = false,
                    PublishedCoords = null
                }
            };
        }

        private static async Task AwaitStopAsync(Task runTask)
        {
            try
            {
                await runTask;
            }
            catch (OperationCanceledException)
            {
            }
        }

        private sealed class ThrowingLocalizer : ILocalizer
        {
            public RtLocalizationPrediction Predict(RtLocalizationRequest request, CancellationToken cancellationToken)
            {
                throw new InvalidOperationException("boom");
            }
        }

        private sealed class StableLocalizer : ILocalizer
        {
            public RtLocalizationPrediction Predict(RtLocalizationRequest request, CancellationToken cancellationToken)
            {
                return new RtLocalizationPrediction
                {
                    IsOutOfDistribution = false,
                    MahalanobisDistance = 1.0,
                    ClassifierProbability = 0.9,
                    Label = "Single",
                    PredictedVector = new[] { 1.0, 2.0, 3.0 }
                };
            }
        }

        private sealed class TempDirectory : IDisposable
        {
            public TempDirectory()
            {
                Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), System.Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(Path);
            }

            public string Path { get; }

            public void Dispose()
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, recursive: true);
                }
            }
        }
    }
}
