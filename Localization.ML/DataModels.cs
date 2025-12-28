using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using Microsoft.ML.Data;

namespace Localization.ML;

public sealed class LocalizationRow
{
    public required double[] Channels { get; init; }
    public double? DurationSeconds { get; init; }
    public bool IsDual { get; init; }
    public double[]? SingleCoordinates { get; init; }
    public double[]? DualCoordinates { get; init; }
    public IReadOnlyDictionary<string, string>? Metadata { get; init; }
}

public sealed class ClassificationExample
{
    [LoadColumn(0)]
    public bool Label { get; set; }

    [VectorType(FeatureBuilder.ExpectedFeatureCount)]
    public float[] Features { get; set; } = Array.Empty<float>();
}

public sealed class RegressionExample
{
    [LoadColumn(0)]
    public float Label { get; set; }

    [VectorType(FeatureBuilder.ExpectedFeatureCount)]
    public float[] Features { get; set; } = Array.Empty<float>();
}

public sealed class ClassificationPrediction
{
    [ColumnName("PredictedLabel")]
    public bool PredictedLabel { get; set; }

    public float Probability { get; set; }
    public float Score { get; set; }
}

public sealed class RegressionPrediction
{
    public float Score { get; set; }
}

public sealed class PredictionDiagnostics
{
    public double MahalanobisDistance { get; init; }
    public bool IsOutOfDistribution { get; init; }
    public IReadOnlyList<string> FeatureNames { get; init; } = Array.Empty<string>();
    public double RawProbability { get; init; }
    public double CalibratedProbability { get; init; }
}

public sealed class PredictionResult
{
    public required string Label { get; init; }
    public required IReadOnlyList<double> Coordinates { get; init; }
    public required double Probability { get; init; }
    public ClassificationPrediction RawClassification { get; init; } = new();
    public PredictionDiagnostics Diagnostics { get; init; } = new();
    public IReadOnlyList<double>? RawClassifierProbabilities { get; init; }
}

public sealed record FeatureImportanceItem(string Feature, double Gain);

public sealed class TrainingSummary
{
    public required double HoldoutAccuracy { get; init; }
    public required double HoldoutPrecision { get; init; }
    public required double HoldoutRecall { get; init; }
    public required double HoldoutF1 { get; init; }
    public SplitMetrics? GroupedHoldoutClassifier { get; init; }
    public required double CrossValidationAccuracyMean { get; init; }
    public required double CrossValidationAccuracyStd { get; init; }
    public required double RandomLabelAccuracy { get; init; }
    public required double SingleRegressorR2 { get; init; }
    public RegressionHoldoutSummary? GroupedHoldoutSingleRegressor { get; init; }
    public required double DualRegressorR2 { get; init; }
    public RegressionHoldoutSummary? GroupedHoldoutDualRegressor { get; init; }
    public required IReadOnlyList<FeatureImportanceItem> FeatureImportance { get; init; }
    public OodDetectorMetrics? OodDetectorMetrics { get; init; }
    public CalibrationReport? Calibration { get; init; }
}

public sealed class SplitMetrics
{
    public required double Accuracy { get; init; }
    public required double Precision { get; init; }
    public required double Recall { get; init; }
    public required double F1 { get; init; }
    public required int TruePositives { get; init; }
    public required int FalsePositives { get; init; }
    public required int TrueNegatives { get; init; }
    public required int FalseNegatives { get; init; }
}

public sealed class RegressionHoldoutSummary
{
    public required double R2 { get; init; }
}

public sealed class PipelineConfiguration
{
    public double Epsilon { get; set; }
    public double OutOfDistributionThreshold { get; set; }
    public double MinimumSeparationCm { get; set; }
    public double StrictProbability { get; set; }
    public string SchemaVersion { get; set; } = string.Empty;
    public string SchemaHash { get; set; } = string.Empty;
    public IReadOnlyList<string> FeatureNames { get; set; } = Array.Empty<string>();
    public IReadOnlyList<string> FeatureColumns { get; set; } = Array.Empty<string>();
    public IReadOnlyList<double> DipolePositions { get; set; } = Array.Empty<double>();
    public string? ClassifierTrainer { get; set; }
    public IReadOnlyList<string>? RegressorTrainers { get; set; }
    public TrainingSummary? TrainingSummary { get; set; }
    public CalibrationReport? Calibration { get; set; }
}

public sealed class OodDetectorMetrics
{
    public double AucRoc { get; init; }
    public double AucPr { get; init; }
    public double SelectedThreshold { get; init; }
    public double FalseAlarmRateAtThreshold { get; init; }
    public double DetectionRateAtThreshold { get; init; }
    public IReadOnlyDictionary<string, double>? DetectionRateByPerturbation { get; init; }
}

public sealed class CalibrationReport
{
    public string CalibratorType { get; init; } = string.Empty;
    public double BrierScoreRaw { get; init; }
    public double BrierScoreCalibrated { get; init; }
    public double? ExpectedCalibrationError { get; init; }
    public int ReliabilityBinCount { get; init; }
}
