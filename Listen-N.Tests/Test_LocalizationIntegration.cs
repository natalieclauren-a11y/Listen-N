using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Integrated.Contracts;
using Integrated.Runtime;
using LocalizationRow = Integrated.Contracts.LocalizationRow;
using RuntimeLocalizationRequest = Integrated.Runtime.LocalizationRequest;
using Xunit;

namespace Listen_N.Tests
{
    public sealed class LocalizationIntegrationTests
    {
        [Fact]
        public async Task AutoTriggerIsSuppressedInHoldState()
        {
            var bridge = LocalizationChannelBridge.Create();
            var pipeline = new FakePipeline();
            var orchestrator = new LocalizationOrchestratorService(
                bridge.Snapshots.Reader,
                bridge.Requests,
                new DefaultLocalizationTriggerPolicy(),
                pipeline);

            using var cts = new CancellationTokenSource();
            var runTask = orchestrator.RunAsync(cts.Token);

            var snapshot = CreateSnapshot(state: "Hold", totalCounts: 50, zy: 2.5);
            await bridge.Snapshots.Writer.WriteAsync(snapshot);
            bridge.Snapshots.Writer.Complete();
            bridge.Requests.Writer.Complete();
            cts.CancelAfter(TimeSpan.FromSeconds(1));

            await AwaitOrchestratorStopAsync(runTask);

            Assert.Empty(pipeline.ProcessedRows);
        }

        [Fact]
        public async Task ManualTriggerRunsEvenWhenHold()
        {
            var bridge = LocalizationChannelBridge.Create();
            var pipeline = new FakePipeline();
            var orchestrator = new LocalizationOrchestratorService(
                bridge.Snapshots.Reader,
                bridge.Requests,
                new NeverTriggerPolicy(),
                pipeline);

            using var cts = new CancellationTokenSource();
            var runTask = orchestrator.RunAsync(cts.Token);

            var snapshot = CreateSnapshot(state: "Hold", totalCounts: 50, zy: 2.5);
            await bridge.Snapshots.Writer.WriteAsync(snapshot, cts.Token);

            await orchestrator.TriggerNowAsync("UserOverride");

            bridge.Snapshots.Writer.Complete();
            bridge.Requests.Writer.Complete();
            cts.CancelAfter(TimeSpan.FromSeconds(1));

            await AwaitOrchestratorStopAsync(runTask);

            Assert.Single(pipeline.ProcessedRows);
        }

        [Fact]
        public async Task ManualTriggerUsesLatestSnapshot()
        {
            var bridge = LocalizationChannelBridge.Create();
            var pipeline = new FakePipeline();
            var orchestrator = new LocalizationOrchestratorService(
                bridge.Snapshots.Reader,
                bridge.Requests,
                new NeverTriggerPolicy(),
                pipeline);

            using var cts = new CancellationTokenSource();
            var runTask = orchestrator.RunAsync(cts.Token);

            var olderSnapshot = CreateSnapshot(state: "Track", totalCounts: 50, zy: 2.5, windowSeconds: 1.0);
            var newerSnapshot = CreateSnapshot(state: "Track", totalCounts: 60, zy: 3.0, windowSeconds: 2.0);

            await bridge.Snapshots.Writer.WriteAsync(olderSnapshot);
            await bridge.Snapshots.Writer.WriteAsync(newerSnapshot);

            await orchestrator.TriggerNowAsync("UserOverride");

            bridge.Snapshots.Writer.Complete();
            bridge.Requests.Writer.Complete();
            cts.CancelAfter(TimeSpan.FromSeconds(1));

            await AwaitOrchestratorStopAsync(runTask);

            Assert.Single(pipeline.ProcessedRows);
            Assert.Equal(2.0f, pipeline.ProcessedRows[0].Duration);
        }

        [Fact]
        public async Task ManualTriggerReturnsProbeRequest()
        {
            var bridge = LocalizationChannelBridge.Create();
            var pipeline = new FakePipeline();
            var workerChannel = Channel.CreateBounded<RuntimeLocalizationRequest>(1);
            var episodePolicy = new LocalizationEpisodePolicy();
            var orchestrator = new LocalizationOrchestratorService(
                bridge.Snapshots.Reader,
                bridge.Requests,
                new DefaultLocalizationTriggerPolicy(),
                pipeline,
                episodePolicy,
                workerChannel);

            var request = await orchestrator.TriggerNowAsync("ManualProbe");

            Assert.NotNull(request);
            Assert.NotEqual(Guid.Empty, request.EpisodeId);
            Assert.True(request.IsProbe);
            Assert.Equal(15, request.Row.Channels.Count);
        }

        private static async Task AwaitOrchestratorStopAsync(Task runTask, int timeoutMs = 2000)
        {
            var completed = await Task.WhenAny(runTask, Task.Delay(timeoutMs));
            if (completed != runTask)
            {
                throw new TimeoutException("Orchestrator did not stop within timeout.");
            }

            try { await runTask; }
            catch (OperationCanceledException) { }
        }


        private static AnalysisSnapshot CreateSnapshot(string state, int totalCounts = 10, double zy = 0.5, double windowSeconds = 0.5)
        {
            return new AnalysisSnapshot(
                SnapshotId: Guid.NewGuid(),
                TimestampUtc: DateTime.UtcNow,
                WindowDurationSeconds: windowSeconds,
                TubeCounts: new[]
                {
                    1, 1, 1, 1, 1,
                    1, 1, 1, 1, 1,
                    1, 1, 1, 1, 1
                },
                TotalCounts: totalCounts,
                FsmState: state,
                SelectedGateMicroseconds: 10,
                Y: 0.0,
                SigmaY: 1.0,
                Zy: zy,
                ChangeDetected: false,
                HoldLike: state.Equals("Hold", StringComparison.OrdinalIgnoreCase),
                DegradedLike: false,
                SchemaVersion: 1,
                SchemaHash: "v1",
                Diagnostics: null);
        }

        private sealed class NeverTriggerPolicy : ILocalizationTriggerPolicy
        {
            public bool ShouldTrigger(AnalysisSnapshot snapshot) => false;
        }

        private sealed class FakePipeline : ILocalizationPipeline
        {
            public List<LocalizationRow> ProcessedRows { get; } = new();

            public Task<LocalizationResult> RunAsync(LocalizationRow row, CancellationToken ct)
            {
                ProcessedRows.Add(row);
                return Task.FromResult(new LocalizationResult
                {
                    SnapshotId = Guid.Empty,
                    TimestampUtc = DateTime.UnixEpoch,
                    TriggerReason = "FakePipeline",
                    TriggerSource = LocalizationTriggerSource.Manual,
                    PredictedLabel = "Test",
                    Confidence = 0.5f,
                    Ood = false,
                    Mahalanobis = 0.0f,
                    Diagnostics = new Dictionary<string, string>
                    {
                        ["model"] = "fake"
                    }
                });
            }
        }
    }
}
