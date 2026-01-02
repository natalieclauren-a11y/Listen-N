using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Integrated.Contracts;
using Integrated.Runtime;
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

            var runTask = orchestrator.RunAsync(CancellationToken.None);

            var snapshot = CreateSnapshot(state: "Hold");
            await bridge.Snapshots.Writer.WriteAsync(snapshot);
            bridge.Snapshots.Writer.Complete();
            bridge.Requests.Writer.Complete();

            await runTask;

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
                new DefaultLocalizationTriggerPolicy(),
                pipeline);

            var cts = new CancellationTokenSource();
            var runTask = orchestrator.RunAsync(cts.Token);

            var snapshot = CreateSnapshot(state: "Hold", totalCounts: 50, zy: 2.5);
            await bridge.Snapshots.Writer.WriteAsync(snapshot, cts.Token);

            await orchestrator.TriggerNowAsync("UserOverride");

            bridge.Snapshots.Writer.Complete();
            bridge.Requests.Writer.Complete();
            cts.CancelAfter(TimeSpan.FromSeconds(1));

            try
            {
                await runTask;
            }
            catch (OperationCanceledException)
            {
            }

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
                new DefaultLocalizationTriggerPolicy(),
                pipeline);

            var runTask = orchestrator.RunAsync(CancellationToken.None);

            var olderSnapshot = CreateSnapshot(state: "Track", totalCounts: 50, zy: 2.5, windowSeconds: 1.0);
            var newerSnapshot = CreateSnapshot(state: "Track", totalCounts: 60, zy: 3.0, windowSeconds: 2.0);

            await bridge.Snapshots.Writer.WriteAsync(olderSnapshot);
            await bridge.Snapshots.Writer.WriteAsync(newerSnapshot);

            await orchestrator.TriggerNowAsync("UserOverride");

            bridge.Snapshots.Writer.Complete();
            bridge.Requests.Writer.Complete();

            await runTask;

            Assert.Single(pipeline.ProcessedRows);
            Assert.Equal(2.0f, pipeline.ProcessedRows[0].Duration);
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
                SigmaY: 0.0,
                Zy: zy,
                ChangeDetected: false,
                HoldLike: state.Equals("Hold", StringComparison.OrdinalIgnoreCase),
                DegradedLike: false,
                SchemaVersion: 1,
                SchemaHash: "v1",
                Diagnostics: null);
        }

        private sealed class FakePipeline : ILocalizationPipeline
        {
            public List<LocalizationRow> ProcessedRows { get; } = new();

            public Task<LocalizationResult> RunAsync(LocalizationRow row, CancellationToken cancellationToken)
            {
                ProcessedRows.Add(row);
                return Task.FromResult(new LocalizationResult
                {
                    SnapshotId = Guid.Empty,
                    TimestampUtc = DateTime.UtcNow,
                    TriggerReason = string.Empty,
                    TriggerSource = LocalizationTriggerSource.Auto,
                    Forced = false
                });
            }
        }
    }
}
