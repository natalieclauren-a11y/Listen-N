using System.Text.Json.Serialization;

namespace Localization.Metrics;

public enum ScenarioType
{
    Static,
    Motion
}

public sealed class RunTruth
{
    public string RunId { get; init; } = string.Empty;
    public ScenarioType ScenarioType { get; init; } = ScenarioType.Static;
    public string TrueLabel { get; init; } = string.Empty;
    public double[]? CoordsTrue { get; init; }
    public string CoordinateFrame { get; init; } = "detector";
    public string CoordinateUnits { get; init; } = "cm";
    public double[]? DetectorOrigin { get; init; }
    public List<MotionChangePoint> ChangePoints { get; init; } = new();
    public Dictionary<string, string> Metadata { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class MotionChangePoint
{
    public double TimeSeconds { get; init; }
    public string LabelAfter { get; init; } = string.Empty;
    public double[] CoordsTrueAfter { get; init; } = Array.Empty<double>();
    public double[]? CoordsTrueBefore { get; init; }
}

public sealed class WindowRecord
{
    public string RunId { get; init; } = string.Empty;
    public int WindowIndex { get; init; }
    public double TStart { get; init; }
    public double TEnd { get; init; }
    public double DurationSeconds { get; init; }
    public int? TotalCounts { get; init; }
    public int[]? ChannelCounts { get; init; }
    public string? RtStateLabel { get; init; }
    public RtMetrics? RtMetrics { get; init; }
    public string PredictedLabel { get; init; } = string.Empty;
    public double Probability { get; init; }
    public double[]? CoordsPred { get; init; }
    public bool IsOod { get; init; }
    public double? MahalanobisDistance { get; init; }
}

public sealed class RtMetrics
{
    public double? Y { get; init; }
    public double? SigmaY { get; init; }
    public double? SelectedGate { get; init; }
    public double? CorrelationTimeEstimate { get; init; }
    public string? StateLabel { get; init; }
}

public sealed class Phase4Options
{
    public double[] CountThresholds { get; init; } = Array.Empty<double>();
    public double[] TimeThresholdsSeconds { get; init; } = Array.Empty<double>();
    public FieldOfViewConfig FieldOfView { get; init; } = new();
    public double StabilityRadiusCm { get; init; } = 10.0;
    public int StabilityConsecutiveWindows { get; init; } = 3;
    public int WarmupWindowCount { get; init; } = 0;
    public string[] WarmupStateLabels { get; init; } = Array.Empty<string>();
    public bool UseRtStateWarmup { get; init; } = true;
    public double? CoordinateScaleToCmOverride { get; init; }
}

public sealed class FieldOfViewConfig
{
    public double[] RadialBinsCm { get; init; } = Array.Empty<double>();
    public EdgeThresholds EdgeThresholds { get; init; } = new();
}

public sealed class EdgeThresholds
{
    public double? X { get; init; }
    public double? Y { get; init; }
    public double? Z { get; init; }
}

public sealed class Phase4Report
{
    public string RunId { get; init; } = string.Empty;
    public IReadOnlyList<WindowMetricsRow> WindowMetrics { get; init; } = Array.Empty<WindowMetricsRow>();
    public ErrorCurveSummary ErrorVsCounts { get; init; } = new();
    public ErrorCurveSummary ErrorVsTime { get; init; } = new();
    public IReadOnlyList<DistanceRegionSummary> ErrorVsDistanceRegion { get; init; } = Array.Empty<DistanceRegionSummary>();
    public MotionLatencyReport? MotionLatency { get; init; }
    public StaticStabilityReport? StaticStability { get; init; }
    public MultiplicitySummary? MultiplicitySummary { get; init; }
    public Phase4Metadata Metadata { get; init; } = new();
}

public sealed class Phase4Metadata
{
    public string CoordinateFrame { get; init; } = "detector";
    public string CoordinateUnits { get; init; } = "cm";
    public double[] DetectorOrigin { get; init; } = new double[] { 0.0, 0.0, 0.0 };
    public Phase4Options Options { get; init; } = new();
}

public sealed class WindowMetricsRow
{
    public string RunId { get; init; } = string.Empty;
    public int WindowIndex { get; init; }
    public double TStart { get; init; }
    public double TEnd { get; init; }
    public double DurationSeconds { get; init; }
    public int TotalCounts { get; init; }
    public double CumulativeCounts { get; init; }
    public double CumulativeTimeSeconds { get; init; }
    public string PredictedLabel { get; init; } = string.Empty;
    public bool IsOod { get; init; }
    public double Probability { get; init; }
    public string TrueLabel { get; init; } = string.Empty;
    public double[] TrueCoords { get; init; } = Array.Empty<double>();
    public double? ErrorCm { get; init; }
    public double? MeanErrorCm { get; init; }
    public double? Error1Cm { get; init; }
    public double? Error2Cm { get; init; }
    public double? CentroidErrorCm { get; init; }
    public double? BiasX { get; init; }
    public double? BiasY { get; init; }
    public double? BiasZ { get; init; }
    public double? Bias1X { get; init; }
    public double? Bias1Y { get; init; }
    public double? Bias1Z { get; init; }
    public double? Bias2X { get; init; }
    public double? Bias2Y { get; init; }
    public double? Bias2Z { get; init; }
    public double? CentroidBiasX { get; init; }
    public double? CentroidBiasY { get; init; }
    public double? CentroidBiasZ { get; init; }
    public double? TrueDistanceCm { get; init; }
    public bool StableFlag { get; init; }
    public double? MahalanobisDistance { get; init; }
    public RtMetrics? RtMetrics { get; init; }
}

public sealed class ErrorCurveSummary
{
    public IReadOnlyList<ErrorCurvePoint> Curve { get; init; } = Array.Empty<ErrorCurvePoint>();
    public IReadOnlyList<ThresholdErrorSummary> ThresholdSummaries { get; init; } = Array.Empty<ThresholdErrorSummary>();
}

public sealed class ErrorCurvePoint
{
    public double CumulativeValue { get; init; }
    public double? ErrorCm { get; init; }
    public bool IsUnknownOrOod { get; init; }
}

public sealed class ThresholdErrorSummary
{
    public double Threshold { get; init; }
    public double? MedianErrorCm { get; init; }
    public double? P90ErrorCm { get; init; }
    public double UnknownFraction { get; init; }
    public int TotalWindows { get; init; }
    public int ErrorWindows { get; init; }
}

public sealed class DistanceRegionSummary
{
    public string RegionKey { get; init; } = string.Empty;
    public double? MedianErrorCm { get; init; }
    public BiasVector MeanBias { get; init; } = new();
    public double UnknownFraction { get; init; }
    public int TotalWindows { get; init; }
    public int ErrorWindows { get; init; }
}

public sealed class MotionLatencyReport
{
    public IReadOnlyList<MotionLatencyResult> ChangePoints { get; init; } = Array.Empty<MotionLatencyResult>();
}

public sealed class MotionLatencyResult
{
    public double ChangePointTime { get; init; }
    public string LabelAfter { get; init; } = string.Empty;
    public double? LatencySeconds { get; init; }
    public double? FirstOutputLatencySeconds { get; init; }
    public bool Success { get; init; }
}

public sealed class StaticStabilityReport
{
    public DispersionStats? Single { get; init; }
    public DualDispersionStats? Dual { get; init; }
    public double? LabelTransitionsPerMinute { get; init; }
    public double UnknownFraction { get; init; }
}

public sealed class DispersionStats
{
    public double? RmsDistanceToMedianCm { get; init; }
    public AxisStats AxisStdDevCm { get; init; } = new();
}

public sealed class DualDispersionStats
{
    public DispersionStats Source1 { get; init; } = new();
    public DispersionStats Source2 { get; init; } = new();
    public DispersionStats Centroid { get; init; } = new();
}

public sealed class AxisStats
{
    public double? X { get; init; }
    public double? Y { get; init; }
    public double? Z { get; init; }
}

public sealed class MultiplicitySummary
{
    public Dictionary<string, MetricSummary> Metrics { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> Grouping { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class MetricSummary
{
    public double? Mean { get; init; }
    public double? Median { get; init; }
    public double? StdDev { get; init; }
    public double? MedianAbsoluteDeviation { get; init; }
}

public readonly struct BiasVector
{
    [JsonConstructor]
    public BiasVector(double x, double y, double z)
    {
        X = x;
        Y = y;
        Z = z;
    }

    public double X { get; }
    public double Y { get; }
    public double Z { get; }
}
