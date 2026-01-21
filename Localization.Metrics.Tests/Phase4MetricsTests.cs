using Localization.Metrics;
using Xunit;

namespace Localization.Metrics.Tests;

public sealed class Phase4MetricsTests
{
    [Fact]
    public void ComputesSingleSourceError()
    {
        var runTruth = new RunTruth
        {
            RunId = "run-1",
            ScenarioType = ScenarioType.Static,
            TrueLabel = "Single",
            CoordsTrue = new[] { 0.0, 0.0, 0.0 }
        };

        var window = new WindowRecord
        {
            RunId = "run-1",
            WindowIndex = 0,
            TStart = 0.0,
            TEnd = 1.0,
            DurationSeconds = 1.0,
            TotalCounts = 10,
            PredictedLabel = "Single",
            Probability = 0.9,
            CoordsPred = new[] { 3.0, 4.0, 0.0 },
            IsOod = false
        };

        var report = new Phase4Metrics().ComputeAll(runTruth, new[] { window }, new Phase4Options());
        var row = report.WindowMetrics.Single();

        Assert.Equal(5.0, row.ErrorCm!.Value, 6);
        Assert.Equal(3.0, row.BiasX!.Value, 6);
        Assert.Equal(4.0, row.BiasY!.Value, 6);
        Assert.Equal(0.0, row.BiasZ!.Value, 6);
    }

    [Fact]
    public void DualPairingChoosesMinimumTotalError()
    {
        var runTruth = new RunTruth
        {
            RunId = "run-2",
            ScenarioType = ScenarioType.Static,
            TrueLabel = "Dual",
            CoordsTrue = new[] { 0.0, 0.0, 0.0, 10.0, 0.0, 0.0 }
        };

        var window = new WindowRecord
        {
            RunId = "run-2",
            WindowIndex = 0,
            TStart = 0.0,
            TEnd = 1.0,
            DurationSeconds = 1.0,
            TotalCounts = 10,
            PredictedLabel = "Dual",
            Probability = 0.9,
            CoordsPred = new[] { 10.0, 0.0, 0.0, 0.0, 0.0, 0.0 },
            IsOod = false
        };

        var report = new Phase4Metrics().ComputeAll(runTruth, new[] { window }, new Phase4Options());
        var row = report.WindowMetrics.Single();

        Assert.Equal(0.0, row.Error1Cm!.Value, 6);
        Assert.Equal(0.0, row.Error2Cm!.Value, 6);
        Assert.Equal(0.0, row.MeanErrorCm!.Value, 6);
    }

    [Fact]
    public void UnknownOrOodYieldsNullErrorsAndCountsUnknownFraction()
    {
        var runTruth = new RunTruth
        {
            RunId = "run-3",
            ScenarioType = ScenarioType.Static,
            TrueLabel = "Single",
            CoordsTrue = new[] { 0.0, 0.0, 0.0 }
        };

        var windows = new[]
        {
            new WindowRecord
            {
                RunId = "run-3",
                WindowIndex = 0,
                TStart = 0.0,
                TEnd = 1.0,
                DurationSeconds = 1.0,
                TotalCounts = 10,
                PredictedLabel = "Unknown",
                Probability = 0.1,
                CoordsPred = Array.Empty<double>(),
                IsOod = false
            },
            new WindowRecord
            {
                RunId = "run-3",
                WindowIndex = 1,
                TStart = 1.0,
                TEnd = 2.0,
                DurationSeconds = 1.0,
                TotalCounts = 10,
                PredictedLabel = "Single",
                Probability = 0.6,
                CoordsPred = new[] { 1.0, 0.0, 0.0 },
                IsOod = true
            }
        };

        var options = new Phase4Options
        {
            CountThresholds = new[] { 0.0 }
        };

        var report = new Phase4Metrics().ComputeAll(runTruth, windows, options);
        Assert.All(report.WindowMetrics, row => Assert.Null(row.ErrorCm));

        var summary = report.ErrorVsCounts.ThresholdSummaries.Single();
        Assert.Equal(1.0, summary.UnknownFraction, 6);
    }

    [Fact]
    public void MotionLatencyDetectsFirstStableStreak()
    {
        var runTruth = new RunTruth
        {
            RunId = "run-4",
            ScenarioType = ScenarioType.Motion,
            TrueLabel = "Single",
            CoordsTrue = new[] { 10.0, 0.0, 0.0 },
            ChangePoints = new List<MotionChangePoint>
            {
                new()
                {
                    TimeSeconds = 5.0,
                    LabelAfter = "Single",
                    CoordsTrueAfter = new[] { 0.0, 0.0, 0.0 }
                }
            }
        };

        var windows = new[]
        {
            new WindowRecord
            {
                RunId = "run-4",
                WindowIndex = 0,
                TStart = 0.0,
                TEnd = 2.0,
                DurationSeconds = 2.0,
                TotalCounts = 10,
                PredictedLabel = "Unknown",
                Probability = 0.0,
                CoordsPred = Array.Empty<double>(),
                IsOod = false
            },
            new WindowRecord
            {
                RunId = "run-4",
                WindowIndex = 1,
                TStart = 4.0,
                TEnd = 6.0,
                DurationSeconds = 2.0,
                TotalCounts = 10,
                PredictedLabel = "Single",
                Probability = 0.5,
                CoordsPred = new[] { 5.0, 0.0, 0.0 },
                IsOod = false
            },
            new WindowRecord
            {
                RunId = "run-4",
                WindowIndex = 2,
                TStart = 6.0,
                TEnd = 8.0,
                DurationSeconds = 2.0,
                TotalCounts = 10,
                PredictedLabel = "Single",
                Probability = 0.9,
                CoordsPred = new[] { 0.5, 0.0, 0.0 },
                IsOod = false
            },
            new WindowRecord
            {
                RunId = "run-4",
                WindowIndex = 3,
                TStart = 8.0,
                TEnd = 10.0,
                DurationSeconds = 2.0,
                TotalCounts = 10,
                PredictedLabel = "Single",
                Probability = 0.9,
                CoordsPred = new[] { 0.4, 0.0, 0.0 },
                IsOod = false
            }
        };

        var options = new Phase4Options
        {
            StabilityRadiusCm = 1.0,
            StabilityConsecutiveWindows = 2
        };

        var report = new Phase4Metrics().ComputeAll(runTruth, windows, options);
        var latency = report.MotionLatency!.ChangePoints.Single();

        Assert.Equal(3.0, latency.FirstOutputLatencySeconds!.Value, 6);
        Assert.Equal(3.0, latency.LatencySeconds!.Value, 6);
        Assert.True(latency.Success);
    }

    [Fact]
    public void StabilityMetricsComputeDispersion()
    {
        var runTruth = new RunTruth
        {
            RunId = "run-5",
            ScenarioType = ScenarioType.Static,
            TrueLabel = "Single",
            CoordsTrue = new[] { 0.0, 0.0, 0.0 }
        };

        var windows = new[]
        {
            BuildWindow("run-5", 0, 0.0, 1.0, new[] { 1.0, 1.0, 1.0 }),
            BuildWindow("run-5", 1, 1.0, 2.0, new[] { 2.0, 1.0, 1.0 }),
            BuildWindow("run-5", 2, 2.0, 3.0, new[] { 3.0, 1.0, 1.0 })
        };

        var report = new Phase4Metrics().ComputeAll(runTruth, windows, new Phase4Options());
        var dispersion = report.StaticStability!.Single!;

        Assert.Equal(Math.Sqrt(2.0 / 3.0), dispersion.RmsDistanceToMedianCm!.Value, 6);
        Assert.Equal(Math.Sqrt(2.0 / 3.0), dispersion.AxisStdDevCm.X!.Value, 6);
        Assert.Equal(0.0, dispersion.AxisStdDevCm.Y!.Value, 6);
        Assert.Equal(0.0, dispersion.AxisStdDevCm.Z!.Value, 6);
    }

    [Fact]
    public void ErrorVsCountsBinningComputesExpectedMedians()
    {
        var runTruth = new RunTruth
        {
            RunId = "run-6",
            ScenarioType = ScenarioType.Static,
            TrueLabel = "Single",
            CoordsTrue = new[] { 0.0, 0.0, 0.0 }
        };

        var windows = new[]
        {
            BuildWindow("run-6", 0, 0.0, 1.0, new[] { 1.0, 0.0, 0.0 }),
            BuildWindow("run-6", 1, 1.0, 2.0, new[] { 2.0, 0.0, 0.0 }),
            BuildWindow("run-6", 2, 2.0, 3.0, new[] { 3.0, 0.0, 0.0 })
        };

        var options = new Phase4Options
        {
            CountThresholds = new[] { 10.0, 25.0 }
        };

        var report = new Phase4Metrics().ComputeAll(runTruth, windows, options);
        var summaries = report.ErrorVsCounts.ThresholdSummaries;

        Assert.Equal(2, summaries.Count);
        Assert.Equal(2.0, summaries[0].MedianErrorCm!.Value, 6);
        Assert.Equal(2.8, summaries[0].P90ErrorCm!.Value, 6);
        Assert.Equal(3.0, summaries[1].MedianErrorCm!.Value, 6);
    }

    private static WindowRecord BuildWindow(string runId, int index, double tStart, double tEnd, double[] coords)
    {
        return new WindowRecord
        {
            RunId = runId,
            WindowIndex = index,
            TStart = tStart,
            TEnd = tEnd,
            DurationSeconds = tEnd - tStart,
            TotalCounts = 10,
            PredictedLabel = "Single",
            Probability = 0.9,
            CoordsPred = coords,
            IsOod = false
        };
    }
}
