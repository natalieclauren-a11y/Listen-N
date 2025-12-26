using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.ML;
using Microsoft.ML.Data;
using Microsoft.ML.Trainers.FastTree;
using Localization.ML;
using OxyPlot;
using OxyPlot.Axes;
using OxyPlot.Series;
using OxyPlot.SkiaSharp;

namespace Localization.Train;

internal static class Program
{
    private static readonly JsonSerializerOptions JsonWithNamedFloats = new()
    {
        WriteIndented = true,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals
    };

    private static readonly string[] SingleGroups =
    {
        "Cf_30_Second_LMX",
        "Single_60_Second_Cf"
    };

    private static readonly string[] DualGroups =
    {
        "Dual_Cf_30_Second",
        "Dual_Cf_60_Second_LMX"
    };

    private static readonly Dictionary<string, string> PairMetadataGroups = new(StringComparer.OrdinalIgnoreCase)
    {
        { "Dual_Cf_30_Second", "pair_metadata_30" },
        { "Dual_Cf_60_Second_LMX", "pair_metadata_60" }
    };

    private sealed record ErrorDiagnosticPoint(double X, double ErrorCm, string Regime);

    public static void Main(string[] args)
    {
        var (dataDir, outputDir, durationOverride, useGroupedSplit, validateOod, oodFaultFraction, oodSeed, runNegativeControls, negativeControlSeed, runPermutationControl, runLabelShuffleControl) = ParseArgs(args);
        Directory.CreateDirectory(outputDir);

        var trainer = new ModelTrainer();
        var featureBuilder = trainer.FeatureBuilder;

        var singleRows = LoadSingleGroups(dataDir, durationOverride);
        var dualRows = LoadDualGroups(dataDir, durationOverride);
        var allRows = singleRows.Concat(dualRows).ToList();

        Console.WriteLine($"Loaded {singleRows.Count} single-source rows and {dualRows.Count} dual-source rows");

        var classificationExamples = allRows.Select(r =>
        {
            var features = featureBuilder.BuildFeatures(r.Channels, r.DurationSeconds).FeatureVector;
            return (Row: r, Example: new ClassificationExample { Label = r.IsDual, Features = features.Select(f => (float)f).ToArray() });
        }).ToList();

        if (classificationExamples.Count == 0)
        {
            throw new InvalidOperationException("No training rows loaded. Check --data-dir and available dataset groups.");
        }

        var allClassificationExamples = classificationExamples.Select(c => c.Example).ToList();

        const int reliabilityBins = 10;

        var split = StratifiedThreeWaySplit(classificationExamples, calibrationFraction: 0.0, testFraction: 0.2, seed: 42);
        var (rowClassifierModel, rowMetrics, importances) = trainer.TrainClassifier(split.Train.Select(c => c.Example).ToList(), split.Test.Select(c => c.Example).ToList());
        var cv = trainer.CrossValidateClassifier(allClassificationExamples);
        var randomCheck = trainer.RandomLabelSanityCheck(allClassificationExamples);

        var rowPredictions = BuildPredictions(trainer, rowClassifierModel, split.Test.Select(c => c.Example).ToList());
        var rowSplitMetrics = BuildSplitMetrics(rowMetrics, rowPredictions);

        SplitMetrics? groupedSplitMetrics = null;
        BinaryClassificationMetrics? groupedMlNetMetrics = null;
        ITransformer classifierHoldoutModel = rowClassifierModel;
        var featureImportances = importances;
        var holdoutPredictions = rowPredictions;
        (IReadOnlyList<LocalizationRow> Train, IReadOnlyList<LocalizationRow> Holdout)? groupedSplit = null;

        if (useGroupedSplit)
        {
            var groupedSplitData = Grouping.GroupSplit(allRows, 0.2, 42);
            groupedSplit = (groupedSplitData.Train, groupedSplitData.Holdout);
            var groupedTrain = groupedSplitData.Train.Select(r => BuildClassificationExample(featureBuilder, r)).ToList();
            var groupedHoldoutExamples = groupedSplitData.Holdout.Select(r => BuildClassificationExample(featureBuilder, r)).ToList();

            if (groupedTrain.Count == 0 || groupedHoldoutExamples.Count == 0)
            {
                Console.WriteLine("Warning: grouped split produced empty train or holdout set; skipping grouped evaluation.");
            }
            else
            {
                var (groupedModel, groupedMetrics, groupedImportances) = trainer.TrainClassifier(groupedTrain, groupedHoldoutExamples);
                var groupedPredictions = BuildPredictions(trainer, groupedModel, groupedHoldoutExamples);
                groupedSplitMetrics = BuildSplitMetrics(groupedMetrics, groupedPredictions);
                groupedMlNetMetrics = groupedMetrics;
                classifierHoldoutModel = groupedModel;
                featureImportances = groupedImportances;
                holdoutPredictions = groupedPredictions;
            }
        }

        var probabilitySamples = holdoutPredictions
            .Select(p => (Probability: (double)p.Prediction.Probability, p.Label))
            .ToList();
        var brier = CalibrationModel.ComputeBrierScore(probabilitySamples);
        var ece = CalibrationModel.ComputeExpectedCalibrationError(probabilitySamples, reliabilityBins, p => p);
        var reliability = CalibrationModel.BuildReliabilityBins(probabilitySamples, reliabilityBins, p => p);

        Console.WriteLine("Classifier holdout metrics (ML.NET):");
        PrintClassifierMetrics(rowMetrics, rowSplitMetrics);

        if (groupedSplitMetrics != null && groupedMlNetMetrics != null)
        {
            Console.WriteLine("Classifier grouped-holdout metrics:");
            PrintClassifierMetrics(groupedMlNetMetrics, groupedSplitMetrics);
        }

        Console.WriteLine($"Cross-validation accuracy: mean={cv.Mean:F3}, std={cv.Std:F3}");
        Console.WriteLine($"Random-label sanity accuracy: {randomCheck:F3}");
        Console.WriteLine("Top feature importances (AUC gain):");
        foreach (var item in featureImportances.OrderByDescending(i => i.Gain).Take(10))
        {
            Console.WriteLine($"  {item.Feature}: {item.Gain:F5}");
        }

        Console.WriteLine("Reliability holdout (ML.NET Platt-calibrated probability | label):");
        foreach (var sample in holdoutPredictions.Take(5))
        {
            Console.WriteLine($"  {sample.Prediction.Probability:F4} | label={(sample.Label ? 1 : 0)}");
        }

        var reliabilityPlot = BuildReliabilityDiagram(
            reliability,
            brier,
            reliabilityBins,
            "Platt-calibrated (ML.NET) Probability");
        var reliabilityPath = Path.Combine(outputDir, "reliability_diagram.png");
        using (var stream = File.Open(reliabilityPath, FileMode.Create))
        {
            new PngExporter { Width = 900, Height = 600 }.Export(reliabilityPlot, stream);
        }

        // Regression datasets
        var singleFeatures = singleRows.Select(r => featureBuilder.BuildFeatures(r.Channels, r.DurationSeconds).FeatureVector.Select(f => (float)f).ToArray()).ToList();
        var singleTargets = singleRows.Select(r => r.SingleCoordinates!).ToList();
        var dualFeatures = dualRows.Select(r => featureBuilder.BuildFeatures(r.Channels, r.DurationSeconds).FeatureVector.Select(f => (float)f).ToArray()).ToList();
        var dualTargets = dualRows.Select(r => r.DualCoordinates!).ToList();

        if (singleFeatures.Count == 0)
        {
            Console.WriteLine("Warning: no single-source regression rows loaded; training a fallback regressor with zeros.");
        }

        if (dualFeatures.Count == 0)
        {
            Console.WriteLine("Warning: no dual-source regression rows loaded; training a fallback regressor with zeros.");
        }

        double singleR2 = singleFeatures.Count == 0 ? double.NaN : TrainWithHoldout(trainer, singleFeatures, singleTargets, new[] { "x", "y", "z" });
        double dualR2 = dualFeatures.Count == 0 ? double.NaN : TrainWithHoldout(trainer, dualFeatures, dualTargets, new[] { "x1", "y1", "z1", "x2", "y2", "z2" });
        double groupedSingleR2 = double.NaN;
        double groupedDualR2 = double.NaN;

        if (useGroupedSplit)
        {
            groupedSingleR2 = singleFeatures.Count == 0 ? double.NaN : TrainWithGroupedHoldout(trainer, singleRows, featureBuilder, new[] { "x", "y", "z" }, r => r.SingleCoordinates!);
            groupedDualR2 = dualFeatures.Count == 0 ? double.NaN : TrainWithGroupedHoldout(trainer, dualRows, featureBuilder, new[] { "x1", "y1", "z1", "x2", "y2", "z2" }, r => r.DualCoordinates!);
        }

        Console.WriteLine($"Single-source regressor R^2 (mean): {singleR2:F3}");
        if (useGroupedSplit)
        {
            Console.WriteLine($"Single-source regressor grouped R^2 (mean): {groupedSingleR2:F3}");
        }
        Console.WriteLine($"Dual-source regressor R^2 (mean): {dualR2:F3}");
        if (useGroupedSplit)
        {
            Console.WriteLine($"Dual-source regressor grouped R^2 (mean): {groupedDualR2:F3}");
        }

        // Fit final regressors on all data
        var singleRegressor = singleFeatures.Count == 0
            ? TrainFallbackRegressor(trainer, featureBuilder, durationOverride, new[] { "x", "y", "z" })
            : trainer.TrainMultiRegressor(singleFeatures, singleTargets, new[] { "x", "y", "z" });
        var dualRegressor = dualFeatures.Count == 0
            ? TrainFallbackRegressor(trainer, featureBuilder, durationOverride, new[] { "x1", "y1", "z1", "x2", "y2", "z2" })
            : trainer.TrainMultiRegressor(dualFeatures, dualTargets, new[] { "x1", "y1", "z1", "x2", "y2", "z2" });

        // OOD scoring
        var oodFeatures = classificationExamples.Select(c => ExtractOodVector(c.Example.Features)).ToList();
        var mahalanobis = MahalanobisScorer.FromSamples(oodFeatures.Select(v => v.Select(x => (double)x).ToArray()));
        var oodDistances = oodFeatures.Select(v => mahalanobis.Score(v.Select(x => (double)x).ToArray())).ToList();
        double oodMean = oodDistances.Average();
        double oodStd = Math.Sqrt(oodDistances.Average(d => Math.Pow(d - oodMean, 2)));
        double oodThreshold = oodMean + 3 * oodStd;
        OodDetectorMetrics? oodMetrics = null;

        if (validateOod && split.Test.Count > 0)
        {
            Console.WriteLine("OOD detector validation (Mahalanobis distance):");
            var holdoutRows = split.Test.Select(r => r.Row).ToList();
            var oodSamples = BuildOodSamples(holdoutRows, featureBuilder, mahalanobis, oodFaultFraction, oodSeed);
            var evaluation = ComputeOodCurves(oodSamples, oodThreshold);

            Console.WriteLine($"  AUC ROC: {evaluation.AucRoc:F3}");
            Console.WriteLine($"  AUC PR: {evaluation.AucPr:F3}");
            Console.WriteLine($"  Threshold={oodThreshold:F4}: FPR={evaluation.ThresholdFpr:F3}, TPR={evaluation.ThresholdTpr:F3}");
            foreach (var kvp in evaluation.DetectionRateByType)
            {
                Console.WriteLine($"    {kvp.Key} detection rate: {kvp.Value:F3}");
            }

            var rocPlot = BuildRocPlot(evaluation.RocPoints, evaluation.ThresholdFpr, evaluation.ThresholdTpr);
            var prPlot = BuildPrPlot(evaluation.PrPoints, evaluation.ThresholdRecall, evaluation.ThresholdPrecision);

            var rocPath = Path.Combine(outputDir, "ood_roc.png");
            var prPath = Path.Combine(outputDir, "ood_pr.png");
            using (var stream = File.Open(rocPath, FileMode.Create))
            {
                new PngExporter { Width = 900, Height = 600 }.Export(rocPlot, stream);
            }

            using (var stream = File.Open(prPath, FileMode.Create))
            {
                new PngExporter { Width = 900, Height = 600 }.Export(prPlot, stream);
            }

            oodMetrics = new OodDetectorMetrics
            {
                AucRoc = evaluation.AucRoc,
                AucPr = evaluation.AucPr,
                SelectedThreshold = oodThreshold,
                FalseAlarmRateAtThreshold = evaluation.ThresholdFpr,
                DetectionRateAtThreshold = evaluation.ThresholdTpr,
                DetectionRateByPerturbation = evaluation.DetectionRateByType
            };
        }

        var calibrationReport = new CalibrationReport
        {
            CalibratorType = "MLNetPlatt",
            BrierScoreRaw = brier,
            BrierScoreCalibrated = brier,
            ExpectedCalibrationError = ece,
            ReliabilityBinCount = reliabilityBins
        };

        var summary = new TrainingSummary
        {
            HoldoutAccuracy = rowMetrics.Accuracy,
            HoldoutPrecision = rowMetrics.PositivePrecision,
            HoldoutRecall = rowMetrics.PositiveRecall,
            HoldoutF1 = rowMetrics.F1Score,
            GroupedHoldoutClassifier = groupedSplitMetrics,
            CrossValidationAccuracyMean = cv.Mean,
            CrossValidationAccuracyStd = cv.Std,
            RandomLabelAccuracy = randomCheck,
            SingleRegressorR2 = singleR2,
            GroupedHoldoutSingleRegressor = !useGroupedSplit || double.IsNaN(groupedSingleR2) ? null : new RegressionHoldoutSummary { R2 = groupedSingleR2 },
            DualRegressorR2 = dualR2,
            GroupedHoldoutDualRegressor = !useGroupedSplit || double.IsNaN(groupedDualR2) ? null : new RegressionHoldoutSummary { R2 = groupedDualR2 },
            FeatureImportance = featureImportances,
            OodDetectorMetrics = oodMetrics,
            Calibration = calibrationReport
        };

        var config = new PipelineConfiguration
        {
            Epsilon = featureBuilder.Epsilon,
            OutOfDistributionThreshold = oodThreshold,
            MinimumSeparationCm = 8,
            StrictProbability = 0.98,
            FeatureNames = featureBuilder.FeatureNames,
            DipolePositions = featureBuilder.DipolePositions,
            TrainingSummary = summary,
            Calibration = calibrationReport
        };

        var pipeline = new LocalizationPipeline(trainer.MlContext, featureBuilder, classifierHoldoutModel, singleRegressor, dualRegressor, mahalanobis, config);
        pipeline.Save(outputDir);

        File.WriteAllText(Path.Combine(outputDir, "training_summary.json"), JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Artifacts saved to {outputDir}");

        var randomHoldoutRows = split.Test.Select(t => t.Row).ToList();
        SaveErrorDiagnostics(
            pipeline,
            randomHoldoutRows,
            "Random holdout",
            Path.Combine(outputDir, "error_vs_distance_random.png"),
            Path.Combine(outputDir, "error_vs_counts_random.png"),
            Path.Combine(outputDir, "error_vs_distance_random.json"),
            Path.Combine(outputDir, "error_vs_counts_random.json"));

        if (groupedSplit is { Holdout: { } groupedHoldout } && groupedHoldout.Count > 0)
        {
            SaveErrorDiagnostics(
                pipeline,
                groupedHoldout,
                "Grouped holdout",
                Path.Combine(outputDir, "error_vs_distance_grouped.png"),
                Path.Combine(outputDir, "error_vs_counts_grouped.png"),
                Path.Combine(outputDir, "error_vs_distance_grouped.json"),
                Path.Combine(outputDir, "error_vs_counts_grouped.json"));
        }

        if (runPermutationControl || runLabelShuffleControl)
        {
            RunNegativeControls(
                trainer,
                pipeline,
                classifierHoldoutModel,
                featureBuilder,
                singleRows,
                dualRows,
                split,
                outputDir,
                negativeControlSeed,
                runPermutationControl,
                runLabelShuffleControl);
        }
    }

    private static double TrainWithHoldout(ModelTrainer trainer, List<float[]> features, List<double[]> targets, IReadOnlyList<string> targetNames)
    {
        if (features.Count == 0)
        {
            return double.NaN;
        }

        var rnd = new Random(99);
        var indices = Enumerable.Range(0, features.Count).OrderBy(_ => rnd.Next()).ToList();
        int testCount = Math.Max(1, features.Count / 4);
        var testIdx = indices.Take(testCount).ToHashSet();

        var trainFeatures = new List<float[]>();
        var trainTargets = new List<double[]>();
        var testFeatures = new List<float[]>();
        var testTargets = new List<double[]>();

        for (int i = 0; i < features.Count; i++)
        {
            if (testIdx.Contains(i))
            {
                testFeatures.Add(features[i]);
                testTargets.Add(targets[i]);
            }
            else
            {
                trainFeatures.Add(features[i]);
                trainTargets.Add(targets[i]);
            }
        }

        var regressor = trainer.TrainMultiRegressor(trainFeatures, trainTargets, targetNames);
        var r2 = ComputeR2(regressor, testFeatures, testTargets);
        return r2;
    }

    private static double ComputeR2(RegressionModelGroup model, List<float[]> features, List<double[]> targets)
    {
        if (features.Count == 0)
        {
            return double.NaN;
        }

        int dimension = targets[0].Length;
        double[] mean = new double[dimension];
        foreach (var target in targets)
        {
            for (int i = 0; i < dimension; i++)
            {
                mean[i] += target[i];
            }
        }
        for (int i = 0; i < dimension; i++)
        {
            mean[i] /= targets.Count;
        }

        double[] ssTot = new double[dimension];
        double[] ssRes = new double[dimension];

        for (int idx = 0; idx < features.Count; idx++)
        {
            var prediction = model.Predict(features[idx]);
            var target = targets[idx];
            for (int d = 0; d < dimension; d++)
            {
                ssTot[d] += Math.Pow(target[d] - mean[d], 2);
                ssRes[d] += Math.Pow(target[d] - prediction[d], 2);
            }
        }

        double r2Mean = 0;
        int valid = 0;
        for (int d = 0; d < dimension; d++)
        {
            if (ssTot[d] <= 0)
            {
                continue;
            }
            r2Mean += 1 - (ssRes[d] / ssTot[d]);
            valid++;
        }

        return valid == 0 ? double.NaN : r2Mean / valid;
    }

    private static (IReadOnlyList<(LocalizationRow Row, ClassificationExample Example)> Train, IReadOnlyList<(LocalizationRow Row, ClassificationExample Example)> Calibration, IReadOnlyList<(LocalizationRow Row, ClassificationExample Example)> Test) StratifiedThreeWaySplit(IReadOnlyList<(LocalizationRow Row, ClassificationExample Example)> data, double calibrationFraction, double testFraction, int seed)
    {
        var grouped = data.GroupBy(d => d.Example.Label).ToDictionary(g => g.Key, g => g.ToList());
        var train = new List<(LocalizationRow Row, ClassificationExample Example)>();
        var calibration = new List<(LocalizationRow Row, ClassificationExample Example)>();
        var test = new List<(LocalizationRow Row, ClassificationExample Example)>();
        var rnd = new Random(seed);

        foreach (var kvp in grouped)
        {
            var shuffled = kvp.Value.OrderBy(_ => rnd.Next()).ToList();
            int calibCount = (int)Math.Round(shuffled.Count * calibrationFraction);
            int testCount = (int)Math.Round(shuffled.Count * testFraction);
            calibCount = Math.Min(calibCount, shuffled.Count);
            testCount = Math.Min(testCount, Math.Max(0, shuffled.Count - calibCount));

            calibration.AddRange(shuffled.Take(calibCount));
            test.AddRange(shuffled.Skip(calibCount).Take(testCount));
            train.AddRange(shuffled.Skip(calibCount + testCount));
        }

        return (train, calibration, test);
    }

    private static PlotModel BuildReliabilityDiagram(IReadOnlyList<ReliabilityBin> bins, double brierScore, int binCount, string probabilityLabel)
    {
        var model = new PlotModel
        {
            Title = $"Reliability Diagram (Brier={brierScore:F3})",
            Subtitle = $"Probability: {probabilityLabel}, Bins={binCount}"
        };

        model.Axes.Add(new LinearAxis
        {
            Position = AxisPosition.Bottom,
            Minimum = 0,
            Maximum = 1,
            Title = "Mean predicted probability"
        });

        model.Axes.Add(new LinearAxis
        {
            Position = AxisPosition.Left,
            Minimum = 0,
            Maximum = 1,
            Title = "Observed fraction positive"
        });

        var ideal = new LineSeries
        {
            Color = OxyColors.Gray,
            StrokeThickness = 1.5,
            LineStyle = LineStyle.Dash
        };
        ideal.Points.Add(new DataPoint(0, 0));
        ideal.Points.Add(new DataPoint(1, 1));
        model.Series.Add(ideal);

        var series = new LineSeries
        {
            Color = OxyColors.SteelBlue,
            MarkerType = MarkerType.Circle,
            MarkerSize = 4,
            StrokeThickness = 2
        };

        foreach (var bin in bins.Where(b => !double.IsNaN(b.MeanPredictedProbability) && !double.IsNaN(b.EmpiricalFraction)))
        {
            series.Points.Add(new DataPoint(bin.MeanPredictedProbability, bin.EmpiricalFraction));
        }

        model.Series.Add(series);
        return model;
    }

    private static float[] ExtractOodVector(IReadOnlyList<float> features)
    {
        int offset = FeatureBuilder.ChannelCount + 1 + FeatureBuilder.ChannelCount;
        return new[]
        {
            features[offset],
            features[offset + 1],
            features[offset + 2],
            features[offset + 3]
        };
    }

    private static List<OodSample> BuildOodSamples(IReadOnlyList<LocalizationRow> holdoutRows, FeatureBuilder featureBuilder, MahalanobisScorer mahalanobis, double faultFraction, int seed)
    {
        var samples = new List<OodSample>();
        var rnd = new Random(seed);

        foreach (var row in holdoutRows)
        {
            samples.Add(ScoreRow(row, false, "Clean"));
        }

        int targetPositives = (int)Math.Round(holdoutRows.Count * Math.Max(0, faultFraction));
        if (targetPositives == 0 && faultFraction > 0 && holdoutRows.Count > 0)
        {
            targetPositives = 1;
        }

        var faultTypes = Enum.GetValues<FaultType>();
        for (int i = 0; i < targetPositives; i++)
        {
            var baseRow = holdoutRows[rnd.Next(holdoutRows.Count)];
            var fault = faultTypes[rnd.Next(faultTypes.Length)];
            var perturbed = ApplyFaultPerturbation(baseRow, fault, rnd);
            samples.Add(ScoreRow(perturbed, true, fault.ToString()));
        }

        return samples;

        OodSample ScoreRow(LocalizationRow row, bool isOod, string perturbation)
        {
            var features = featureBuilder.BuildFeatures(row.Channels, row.DurationSeconds).FeatureVector.Select(f => (float)f).ToArray();
            var oodVector = ExtractOodVector(features);
            double distance = mahalanobis.Score(oodVector.Select(v => (double)v).ToArray());
            return new OodSample(distance, isOod, perturbation);
        }
    }

    private static OodEvaluationResult ComputeOodCurves(IReadOnlyList<OodSample> samples, double threshold)
    {
        int positives = samples.Count(s => s.IsOod);
        int negatives = samples.Count - positives;

        var thresholds = new List<double> { double.PositiveInfinity };
        thresholds.AddRange(samples.Select(s => s.Distance).Distinct().OrderByDescending(d => d));

        var rocPoints = new List<DataPoint>();
        var prPoints = new List<DataPoint>();

        double prevFpr = 0;
        double prevTpr = 0;
        double prevRecall = 0;
        double prevPrecision = 1;
        double aucRoc = 0;
        double aucPr = 0;

        foreach (var thr in thresholds)
        {
            var metrics = EvaluateAt(samples, thr, positives, negatives);
            rocPoints.Add(new DataPoint(metrics.Fpr, metrics.Tpr));
            prPoints.Add(new DataPoint(metrics.Recall, metrics.Precision));

            aucRoc += (metrics.Fpr - prevFpr) * (metrics.Tpr + prevTpr) / 2.0;
            aucPr += (metrics.Recall - prevRecall) * (metrics.Precision + prevPrecision) / 2.0;
            prevFpr = metrics.Fpr;
            prevTpr = metrics.Tpr;
            prevRecall = metrics.Recall;
            prevPrecision = metrics.Precision;
        }

        var thresholdMetrics = EvaluateAt(samples, threshold, positives, negatives);
        var detectionByType = samples
            .Where(s => s.IsOod)
            .GroupBy(s => s.PerturbationType)
            .ToDictionary(
                g => g.Key,
                g => g.Count() == 0 ? double.NaN : (double)g.Count(s => s.Distance >= threshold) / g.Count());

        return new OodEvaluationResult
        {
            RocPoints = rocPoints,
            PrPoints = prPoints,
            AucRoc = double.IsNaN(aucRoc) ? 0 : aucRoc,
            AucPr = double.IsNaN(aucPr) ? 0 : aucPr,
            ThresholdFpr = thresholdMetrics.Fpr,
            ThresholdTpr = thresholdMetrics.Tpr,
            ThresholdPrecision = thresholdMetrics.Precision,
            ThresholdRecall = thresholdMetrics.Recall,
            DetectionRateByType = detectionByType
        };
    }

    private static PlotModel BuildRocPlot(IReadOnlyList<DataPoint> points, double thresholdFpr, double thresholdTpr)
    {
        var model = new PlotModel { Title = "OOD ROC" };
        model.Axes.Add(new LinearAxis { Position = AxisPosition.Bottom, Minimum = 0, Maximum = 1, Title = "False Positive Rate" });
        model.Axes.Add(new LinearAxis { Position = AxisPosition.Left, Minimum = 0, Maximum = 1, Title = "True Positive Rate" });

        var series = new LineSeries { Color = OxyColors.SteelBlue, StrokeThickness = 2 };
        foreach (var point in points.OrderBy(p => p.X))
        {
            series.Points.Add(point);
        }

        var thresholdSeries = new ScatterSeries
        {
            MarkerType = MarkerType.Circle,
            MarkerFill = OxyColors.IndianRed,
            MarkerStroke = OxyColors.DarkRed,
            MarkerStrokeThickness = 1.5,
            MarkerSize = 5
        };
        if (!double.IsNaN(thresholdFpr) && !double.IsNaN(thresholdTpr))
        {
            thresholdSeries.Points.Add(new ScatterPoint(thresholdFpr, thresholdTpr));
        }

        model.Series.Add(series);
        model.Series.Add(thresholdSeries);
        return model;
    }

    private static (double Fpr, double Tpr, double Precision, double Recall) EvaluateAt(IReadOnlyList<OodSample> samples, double threshold, int positives, int negatives)
    {
        int tp = samples.Count(s => s.IsOod && s.Distance >= threshold);
        int fp = samples.Count(s => !s.IsOod && s.Distance >= threshold);
        int fn = samples.Count(s => s.IsOod && s.Distance < threshold);

        double tpr = positives == 0 ? double.NaN : (double)tp / positives;
        double fpr = negatives == 0 ? double.NaN : (double)fp / negatives;
        double precision = tp + fp == 0 ? 1.0 : (double)tp / (tp + fp);
        double recall = positives == 0 ? double.NaN : (double)tp / positives;
        return (fpr, tpr, precision, recall);
    }

    private static LocalizationRow ApplyFaultPerturbation(LocalizationRow row, FaultType fault, Random rnd)
    {
        var channels = row.Channels.ToArray();
        double? duration = row.DurationSeconds;

        switch (fault)
        {
            case FaultType.DeadTube:
            {
                int deadCount = rnd.Next(1, 3);
                foreach (var idx in Enumerable.Range(0, FeatureBuilder.ChannelCount).OrderBy(_ => rnd.Next()).Take(deadCount))
                {
                    channels[idx] = 0;
                }
                break;
            }
            case FaultType.GainDrift:
            {
                int count = rnd.Next(3, 6);
                bool attenuate = rnd.NextDouble() < 0.5;
                double min = attenuate ? 0.5 : 1.2;
                double max = attenuate ? 0.8 : 1.8;
                double factor = min + rnd.NextDouble() * (max - min);
                foreach (var idx in Enumerable.Range(0, FeatureBuilder.ChannelCount).OrderBy(_ => rnd.Next()).Take(count))
                {
                    channels[idx] *= factor;
                }
                break;
            }
            case FaultType.Flattening:
            {
                double mean = channels.Average();
                double alpha = 0.5 + rnd.NextDouble() * 0.4;
                for (int i = 0; i < channels.Length; i++)
                {
                    channels[i] = (1 - alpha) * channels[i] + alpha * mean;
                }
                break;
            }
            case FaultType.DurationMismatch:
            {
                double factor = rnd.NextDouble() < 0.5 ? 0.5 : 2.0;
                duration = (duration ?? 1.0) * factor;
                break;
            }
        }

        return new LocalizationRow
        {
            Channels = channels,
            DurationSeconds = duration,
            IsDual = row.IsDual,
            SingleCoordinates = row.SingleCoordinates,
            DualCoordinates = row.DualCoordinates,
            Metadata = row.Metadata
        };
    }

    private enum FaultType
    {
        DeadTube,
        GainDrift,
        Flattening,
        DurationMismatch
    }

    private sealed record OodSample(double Distance, bool IsOod, string PerturbationType);

    private sealed class OodEvaluationResult
    {
        public required IReadOnlyList<DataPoint> RocPoints { get; init; }
        public required IReadOnlyList<DataPoint> PrPoints { get; init; }
        public required double AucRoc { get; init; }
        public required double AucPr { get; init; }
        public required double ThresholdFpr { get; init; }
        public required double ThresholdTpr { get; init; }
        public required double ThresholdPrecision { get; init; }
        public required double ThresholdRecall { get; init; }
        public required IReadOnlyDictionary<string, double> DetectionRateByType { get; init; }
    }

    private static PlotModel BuildPrPlot(IReadOnlyList<DataPoint> points, double thresholdRecall, double thresholdPrecision)
    {
        var model = new PlotModel { Title = "OOD Precision-Recall" };
        model.Axes.Add(new LinearAxis { Position = AxisPosition.Bottom, Minimum = 0, Maximum = 1, Title = "Recall" });
        model.Axes.Add(new LinearAxis { Position = AxisPosition.Left, Minimum = 0, Maximum = 1, Title = "Precision" });

        var series = new LineSeries { Color = OxyColors.SeaGreen, StrokeThickness = 2 };
        foreach (var point in points.OrderBy(p => p.X))
        {
            series.Points.Add(point);
        }

        var thresholdSeries = new ScatterSeries
        {
            MarkerType = MarkerType.Diamond,
            MarkerFill = OxyColors.DarkOrange,
            MarkerStroke = OxyColors.Brown,
            MarkerStrokeThickness = 1.5,
            MarkerSize = 5
        };
        if (!double.IsNaN(thresholdRecall) && !double.IsNaN(thresholdPrecision))
        {
            thresholdSeries.Points.Add(new ScatterPoint(thresholdRecall, thresholdPrecision));
        }

        model.Series.Add(series);
        model.Series.Add(thresholdSeries);
        return model;
    }

    private static ClassificationExample BuildClassificationExample(FeatureBuilder featureBuilder, LocalizationRow row)
    {
        var features = featureBuilder.BuildFeatures(row.Channels, row.DurationSeconds).FeatureVector;
        return new ClassificationExample { Label = row.IsDual, Features = features.Select(f => (float)f).ToArray() };
    }

    private static ClassificationExample BuildClassificationExample(FeatureBuilder featureBuilder, LocalizationRow row, IReadOnlyList<int>? permutation)
    {
        if (permutation is null)
        {
            return BuildClassificationExample(featureBuilder, row);
        }

        var features = featureBuilder.BuildFeaturesWithPermutation(row.Channels, permutation, row.DurationSeconds).FeatureVector;
        return new ClassificationExample { Label = row.IsDual, Features = features.Select(f => (float)f).ToArray() };
    }

    private static List<(ClassificationPrediction Prediction, bool Label)> BuildPredictions(ModelTrainer trainer, ITransformer model, IReadOnlyList<ClassificationExample> examples)
    {
        var predictionEngine = trainer.MlContext.Model.CreatePredictionEngine<ClassificationExample, ClassificationPrediction>(model);
        return examples.Select(example => (Prediction: predictionEngine.Predict(example), example.Label)).ToList();
    }

    private static SplitMetrics BuildSplitMetrics(BinaryClassificationMetrics metrics, IReadOnlyList<(ClassificationPrediction Prediction, bool Label)> predictions)
    {
        int tp = 0, tn = 0, fp = 0, fn = 0;
        foreach (var sample in predictions)
        {
            if (sample.Prediction.PredictedLabel && sample.Label)
            {
                tp++;
            }
            else if (sample.Prediction.PredictedLabel && !sample.Label)
            {
                fp++;
            }
            else if (!sample.Prediction.PredictedLabel && !sample.Label)
            {
                tn++;
            }
            else
            {
                fn++;
            }
        }

        double manualPrecision = tp + fp == 0 ? double.NaN : (double)tp / (tp + fp);
        double manualRecall = tp + fn == 0 ? double.NaN : (double)tp / (tp + fn);
        double manualF1 = double.IsNaN(manualPrecision) || double.IsNaN(manualRecall) || (manualPrecision + manualRecall) == 0
            ? double.NaN
            : 2 * manualPrecision * manualRecall / (manualPrecision + manualRecall);

        return new SplitMetrics
        {
            Accuracy = metrics.Accuracy,
            Precision = double.IsNaN(manualPrecision) ? metrics.PositivePrecision : manualPrecision,
            Recall = double.IsNaN(manualRecall) ? metrics.PositiveRecall : manualRecall,
            F1 = double.IsNaN(manualF1) ? metrics.F1Score : manualF1,
            TruePositives = tp,
            FalsePositives = fp,
            TrueNegatives = tn,
            FalseNegatives = fn
        };
    }

    private static void PrintClassifierMetrics(BinaryClassificationMetrics metrics, SplitMetrics splitMetrics)
    {
        Console.WriteLine($"  Accuracy: {metrics.Accuracy:F3}");
        Console.WriteLine($"  Precision: {metrics.PositivePrecision:F3}");
        Console.WriteLine($"  Recall: {metrics.PositiveRecall:F3}");
        Console.WriteLine($"  F1: {metrics.F1Score:F3}");
        Console.WriteLine("Manual confusion matrix (PredictedLabel vs. ground truth):");
        Console.WriteLine($"  TP={splitMetrics.TruePositives}, FP={splitMetrics.FalsePositives}, TN={splitMetrics.TrueNegatives}, FN={splitMetrics.FalseNegatives}");
        Console.WriteLine($"  Manual Precision={splitMetrics.Precision:F3}, Recall={splitMetrics.Recall:F3}, F1={splitMetrics.F1:F3}");
        Console.WriteLine("ML.NET confusion matrix (rows=actual [False, True], cols=predicted [False, True]):");
        var matrix = metrics.ConfusionMatrix;
        Console.WriteLine($"  TN={matrix.Counts[0][0]}, FP={matrix.Counts[0][1]}");
        Console.WriteLine($"  FN={matrix.Counts[1][0]}, TP={matrix.Counts[1][1]}");
    }

    private static void RunNegativeControls(
        ModelTrainer trainer,
        LocalizationPipeline pipeline,
        ITransformer classifier,
        FeatureBuilder featureBuilder,
        IReadOnlyList<LocalizationRow> singleRows,
        IReadOnlyList<LocalizationRow> dualRows,
        (IReadOnlyList<(LocalizationRow Row, ClassificationExample Example)> Train, IReadOnlyList<(LocalizationRow Row, ClassificationExample Example)> Calibration, IReadOnlyList<(LocalizationRow Row, ClassificationExample Example)> Test) split,
        string outputDir,
        int negativeControlSeed,
        bool runPermutationControl,
        bool runLabelShuffleControl)
    {
        var allRows = singleRows.Concat(dualRows).ToList();
        var groupedSplit = Grouping.GroupSplit(allRows, 0.2, 42);

        if (runPermutationControl)
        {
            var permutation = BuildDeterministicPermutation(negativeControlSeed);
            Console.WriteLine($"[Negative control] Channel permutation (seed={negativeControlSeed}): {string.Join(",", permutation)}");

            var randomHoldoutRows = split.Test.Select(t => t.Row).ToList();
            var groupedHoldoutRows = groupedSplit.Holdout;

            var baselineRandomClassifier = EvaluateClassifier(trainer, classifier, featureBuilder, randomHoldoutRows, null);
            var permutedRandomClassifier = EvaluateClassifier(trainer, classifier, featureBuilder, randomHoldoutRows, permutation);

            var baselineGroupedClassifier = EvaluateClassifier(trainer, classifier, featureBuilder, groupedHoldoutRows, null);
            var permutedGroupedClassifier = EvaluateClassifier(trainer, classifier, featureBuilder, groupedHoldoutRows, permutation);

            var baselineRandomLocalization = EvaluateLocalization(pipeline, randomHoldoutRows, null);
            var permutedRandomLocalization = EvaluateLocalization(pipeline, randomHoldoutRows, permutation);

            var baselineGroupedLocalization = EvaluateLocalization(pipeline, groupedHoldoutRows, null);
            var permutedGroupedLocalization = EvaluateLocalization(pipeline, groupedHoldoutRows, permutation);

            var channelPermutationSummary = new NegativeControlChannelPermutationSummary
            {
                Seed = negativeControlSeed,
                Permutation = permutation,
                RandomHoldout = BuildNegativeSplit(
                    baselineRandomClassifier,
                    permutedRandomClassifier,
                    baselineRandomLocalization,
                    permutedRandomLocalization),
                GroupedHoldout = BuildNegativeSplit(
                    baselineGroupedClassifier,
                    permutedGroupedClassifier,
                    baselineGroupedLocalization,
                    permutedGroupedLocalization)
            };

            var jsonPath = Path.Combine(outputDir, "negative_control_channel_permutation.json");
            File.WriteAllText(jsonPath, JsonSerializer.Serialize(channelPermutationSummary, JsonWithNamedFloats));

            SaveRocPrComparison(
                baselineRandomClassifier.Probabilities,
                permutedRandomClassifier.Probabilities,
                Path.Combine(outputDir, "negative_control_channel_permutation"),
                "Baseline",
                "Permuted");

            SaveCdfComparison(
                baselineRandomLocalization.SingleErrors,
                permutedRandomLocalization.SingleErrors,
                Path.Combine(outputDir, "negative_control_channel_permutation_single_cdf.png"),
                "Single-source localization error CDF");

            SaveCdfComparison(
                baselineRandomLocalization.DualErrors,
                permutedRandomLocalization.DualErrors,
                Path.Combine(outputDir, "negative_control_channel_permutation_dual_cdf.png"),
                "Dual-source localization error CDF");

            SaveRoutingComparison(
                baselineRandomLocalization.Routing,
                permutedRandomLocalization.Routing,
                Path.Combine(outputDir, "negative_control_channel_permutation_routing.png"));

            const int diagnosticBins = 20;
            var (baselineDistancePoints, baselineCountPoints) = BuildErrorDiagnosticPoints(pipeline, randomHoldoutRows, null);
            var (permutedDistancePoints, permutedCountPoints) = BuildErrorDiagnosticPoints(pipeline, randomHoldoutRows, permutation);

            var baselineDistanceBinned = BinDiagnostics(baselineDistancePoints, diagnosticBins);
            var baselineCountBinned = BinDiagnostics(baselineCountPoints, diagnosticBins);
            var permutedDistanceBinned = BinDiagnostics(permutedDistancePoints, diagnosticBins);
            var permutedCountBinned = BinDiagnostics(permutedCountPoints, diagnosticBins);

            SaveErrorVsXPlot(
                "Negative control: localization error vs. distance (random holdout)",
                "Distance to detector (cm)",
                Path.Combine(outputDir, "negative_control_channel_permutation_error_vs_distance.png"),
                baselineDistanceBinned,
                permutedDistanceBinned);

            SaveErrorVsXPlot(
                "Negative control: localization error vs. total counts (random holdout)",
                "Total counts",
                Path.Combine(outputDir, "negative_control_channel_permutation_error_vs_counts.png"),
                baselineCountBinned,
                permutedCountBinned);

            File.WriteAllText(
                Path.Combine(outputDir, "negative_control_channel_permutation_error_vs_distance.json"),
                JsonSerializer.Serialize(new { Baseline = baselineDistanceBinned, Perturbed = permutedDistanceBinned }, JsonWithNamedFloats));

            File.WriteAllText(
                Path.Combine(outputDir, "negative_control_channel_permutation_error_vs_counts.json"),
                JsonSerializer.Serialize(new { Baseline = baselineCountBinned, Perturbed = permutedCountBinned }, JsonWithNamedFloats));

            Console.WriteLine($"[Negative control] Channel permutation artifacts written to {jsonPath}");
        }

        if (runLabelShuffleControl)
        {
            Console.WriteLine($"[Negative control] Label shuffle sanity check (seed={negativeControlSeed})");

            var shuffledTrain = ShuffleLabels(split.Train.Select(t => t.Example).ToList(), negativeControlSeed);
            var rowHoldout = split.Test.Select(t => t.Example).ToList();

            var (shuffledRowModel, shuffledRowMetrics, _) = trainer.TrainClassifier(shuffledTrain, rowHoldout);
            var rowPredictions = BuildPredictions(trainer, shuffledRowModel, rowHoldout);
            var rowSplitMetrics = BuildSplitMetrics(shuffledRowMetrics, rowPredictions);

            var groupedTrainExamples = groupedSplit.Train.Select(r => BuildClassificationExample(featureBuilder, r)).ToList();
            var groupedHoldoutExamples = groupedSplit.Holdout.Select(r => BuildClassificationExample(featureBuilder, r)).ToList();
            var shuffledGroupedTrain = ShuffleLabels(groupedTrainExamples, negativeControlSeed + 1);
            var (shuffledGroupedModel, shuffledGroupedMetrics, _) = trainer.TrainClassifier(shuffledGroupedTrain, groupedHoldoutExamples);
            var groupedPredictions = BuildPredictions(trainer, shuffledGroupedModel, groupedHoldoutExamples);
            var groupedSplitMetrics = BuildSplitMetrics(shuffledGroupedMetrics, groupedPredictions);

            double rowChance = ComputeMajorityAccuracy(rowHoldout.Select(r => r.Label));
            double groupedChance = ComputeMajorityAccuracy(groupedHoldoutExamples.Select(r => r.Label));

            var labelShuffleSummary = new NegativeControlLabelShuffleSummary
            {
                Seed = negativeControlSeed,
                RandomHoldout = new NegativeControlLabelShuffleSplit
                {
                    MajorityBaselineAccuracy = rowChance,
                    Metrics = BuildClassificationMetrics(shuffledRowMetrics, rowSplitMetrics)
                },
                GroupedHoldout = new NegativeControlLabelShuffleSplit
                {
                    MajorityBaselineAccuracy = groupedChance,
                    Metrics = BuildClassificationMetrics(shuffledGroupedMetrics, groupedSplitMetrics)
                }
            };

            var jsonPath = Path.Combine(outputDir, "negative_control_label_shuffle.json");
            File.WriteAllText(jsonPath, JsonSerializer.Serialize(labelShuffleSummary, JsonWithNamedFloats));

            SaveRocPrComparison(
                rowPredictions.Select(p => (p.Label, (double)p.Prediction.Probability)).ToList(),
                groupedPredictions.Select(p => (p.Label, (double)p.Prediction.Probability)).ToList(),
                Path.Combine(outputDir, "negative_control_label_shuffle"),
                "Random holdout",
                "Grouped holdout");

            Console.WriteLine($"[Negative control] Label shuffle artifacts written to {jsonPath}");
        }

        if (runPermutationControl || runLabelShuffleControl)
        {
            Console.WriteLine("Negative-control expectation: channel permutation should collapse spatial performance; label shuffle should drive classifier toward chance-level accuracy.");
        }
    }

    private static ClassificationMetricsSummary BuildClassificationMetrics(BinaryClassificationMetrics metrics, SplitMetrics split)
    {
        return new ClassificationMetricsSummary
        {
            Accuracy = metrics.Accuracy,
            Precision = metrics.PositivePrecision,
            Recall = metrics.PositiveRecall,
            F1 = metrics.F1Score,
            RocAuc = metrics.AreaUnderRocCurve,
            PrAuc = metrics.AreaUnderPrecisionRecallCurve,
            Confusion = new ConfusionCounts
            {
                TruePositives = split.TruePositives,
                FalsePositives = split.FalsePositives,
                TrueNegatives = split.TrueNegatives,
                FalseNegatives = split.FalseNegatives
            }
        };
    }

    private static NegativeControlSplitResult BuildNegativeSplit(
        (ClassificationMetricsSummary Metrics, IReadOnlyList<(bool Label, double Probability)> Probabilities, SplitMetrics RawSplit) baseline,
        (ClassificationMetricsSummary Metrics, IReadOnlyList<(bool Label, double Probability)> Probabilities, SplitMetrics RawSplit) perturbed,
        LocalizationEvaluationResult baselineLoc,
        LocalizationEvaluationResult perturbedLoc)
    {
        return new NegativeControlSplitResult
        {
            BaselineClassifier = baseline.Metrics,
            PerturbedClassifier = perturbed.Metrics,
            BaselineSingle = SummarizeErrors(baselineLoc.SingleErrors),
            PerturbedSingle = SummarizeErrors(perturbedLoc.SingleErrors),
            BaselineDual = SummarizeErrors(baselineLoc.DualErrors),
            PerturbedDual = SummarizeErrors(perturbedLoc.DualErrors),
            BaselineRouting = baselineLoc.Routing,
            PerturbedRouting = perturbedLoc.Routing
        };
    }

    private static (ClassificationMetricsSummary Metrics, IReadOnlyList<(bool Label, double Probability)> Probabilities, SplitMetrics RawSplit) EvaluateClassifier(
        ModelTrainer trainer,
        ITransformer model,
        FeatureBuilder featureBuilder,
        IReadOnlyList<LocalizationRow> rows,
        IReadOnlyList<int>? permutation)
    {
        var examples = rows.Select(r => BuildClassificationExample(featureBuilder, r, permutation)).ToList();
        var dataView = trainer.MlContext.Data.LoadFromEnumerable(examples);
        var metrics = trainer.MlContext.BinaryClassification.Evaluate(model.Transform(dataView), labelColumnName: nameof(ClassificationExample.Label));
        var predictions = BuildPredictions(trainer, model, examples);
        var splitMetrics = BuildSplitMetrics(metrics, predictions);
        var probs = predictions.Select(p => (p.Label, (double)p.Prediction.Probability)).ToList();
        return (BuildClassificationMetrics(metrics, splitMetrics), probs, splitMetrics);
    }

    private static LocalizationEvaluationResult EvaluateLocalization(LocalizationPipeline pipeline, IReadOnlyList<LocalizationRow> rows, IReadOnlyList<int>? permutation)
    {
        var singleErrors = new List<double>();
        var dualErrors = new List<double>();
        int singleRoute = 0, dualRoute = 0, centroidRoute = 0, unknownRoute = 0;

        foreach (var row in rows)
        {
            var prediction = pipeline.Predict(row, permutation);

            if (prediction.Label.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
            {
                unknownRoute++;
            }
            else if (prediction.Label.Contains("centroid", StringComparison.OrdinalIgnoreCase))
            {
                centroidRoute++;
            }
            else if (prediction.Label.StartsWith("Dual", StringComparison.OrdinalIgnoreCase))
            {
                dualRoute++;
            }
            else
            {
                singleRoute++;
            }

            if (!row.IsDual && row.SingleCoordinates is { Length: 3 } singleTruth)
            {
                var coords = prediction.Coordinates.Take(3).ToArray();
                singleErrors.Add(Euclidean(coords, singleTruth));
            }
            else if (row.IsDual && row.DualCoordinates is { Length: 6 } dualTruth)
            {
                var errors = AssignmentAwareErrors(prediction.Coordinates, dualTruth);
                dualErrors.Add(errors.First);
                dualErrors.Add(errors.Second);
            }
        }

        int total = rows.Count;
        var routing = new RoutingBreakdown
        {
            SingleFraction = total == 0 ? double.NaN : (double)singleRoute / total,
            DualFraction = total == 0 ? double.NaN : (double)dualRoute / total,
            CentroidFraction = total == 0 ? double.NaN : (double)centroidRoute / total,
            UnknownFraction = total == 0 ? double.NaN : (double)unknownRoute / total,
            Total = total
        };

        return new LocalizationEvaluationResult(singleErrors, dualErrors, routing);
    }

    private static LocalizationErrorSummary SummarizeErrors(IReadOnlyList<double> errors)
    {
        if (errors.Count == 0)
        {
            return new LocalizationErrorSummary { Count = 0, Mean = double.NaN, Median = double.NaN, Rmse = double.NaN };
        }

        double mean = errors.Average();
        double rmse = Math.Sqrt(errors.Average(e => e * e));
        var sorted = errors.OrderBy(e => e).ToArray();
        double median = Percentile(sorted, 0.5);
        return new LocalizationErrorSummary { Count = errors.Count, Mean = mean, Median = median, Rmse = rmse };
    }

    private static double Percentile(IReadOnlyList<double> sorted, double percentile)
    {
        if (sorted.Count == 0)
        {
            return double.NaN;
        }

        double position = (sorted.Count - 1) * percentile;
        int lowerIndex = (int)Math.Floor(position);
        int upperIndex = (int)Math.Ceiling(position);

        if (upperIndex >= sorted.Count)
        {
            return sorted[^1];
        }

        double weight = position - lowerIndex;
        return sorted[lowerIndex] * (1 - weight) + sorted[upperIndex] * weight;
    }

    private static (List<ErrorDiagnosticPoint> ByDistance, List<ErrorDiagnosticPoint> ByCounts) BuildErrorDiagnosticPoints(
        LocalizationPipeline pipeline,
        IReadOnlyList<LocalizationRow> rows,
        IReadOnlyList<int>? permutation)
    {
        var byDistance = new List<ErrorDiagnosticPoint>();
        var byCounts = new List<ErrorDiagnosticPoint>();

        foreach (var row in rows)
        {
            if (!row.Channels.Any())
            {
                continue;
            }

            var totalCounts = row.Channels.Sum();
            if (!row.IsDual && row.SingleCoordinates is { Length: 3 } singleTruth)
            {
                var prediction = pipeline.Predict(row, permutation);
                if (prediction.Coordinates.Count < 3)
                {
                    continue;
                }

                double distance = Math.Sqrt(singleTruth[0] * singleTruth[0] + singleTruth[1] * singleTruth[1] + singleTruth[2] * singleTruth[2]);
                double error = Euclidean(prediction.Coordinates.Take(3).ToArray(), singleTruth);
                byDistance.Add(new ErrorDiagnosticPoint(distance, error, "Single"));
                byCounts.Add(new ErrorDiagnosticPoint(totalCounts, error, "Single"));
            }
            else if (row.IsDual && row.DualCoordinates is { Length: 6 } dualTruth)
            {
                var prediction = pipeline.Predict(row, permutation);
                if (prediction.Coordinates.Count < 6)
                {
                    continue;
                }

                var errors = AssignmentAwareErrors(prediction.Coordinates, dualTruth);
                double pairError = (errors.First + errors.Second) / 2.0;

                double r1 = Math.Sqrt(Math.Pow(dualTruth[0], 2) + Math.Pow(dualTruth[1], 2) + Math.Pow(dualTruth[2], 2));
                double r2 = Math.Sqrt(Math.Pow(dualTruth[3], 2) + Math.Pow(dualTruth[4], 2) + Math.Pow(dualTruth[5], 2));
                double distance = Math.Min(r1, r2);

                byDistance.Add(new ErrorDiagnosticPoint(distance, pairError, "Dual"));
                byCounts.Add(new ErrorDiagnosticPoint(totalCounts, pairError, "Dual"));
            }
        }

        return (byDistance, byCounts);
    }

    private static BinnedErrorSummary BinDiagnostics(IReadOnlyList<ErrorDiagnosticPoint> points, int binCount)
    {
        var xCenters = new List<double>();
        var medians = new List<double>();
        var p25 = new List<double>();
        var p75 = new List<double>();
        var counts = new List<int>();
        var lowers = new List<double>();
        var uppers = new List<double>();

        if (points.Count == 0 || binCount <= 0)
        {
            return new BinnedErrorSummary
            {
                XCenter = xCenters,
                MedianError = medians,
                P25Error = p25,
                P75Error = p75,
                Counts = counts,
                BinLower = lowers,
                BinUpper = uppers
            };
        }

        var sorted = points.OrderBy(p => p.X).ToList();
        int total = sorted.Count;

        for (int b = 0; b < binCount; b++)
        {
            int start = (int)Math.Floor(b * total / (double)binCount);
            int end = (int)Math.Floor((b + 1) * total / (double)binCount);
            end = Math.Min(end, total);

            if (end <= start)
            {
                continue;
            }

            var bin = sorted.GetRange(start, end - start);
            var xs = bin.Select(p => p.X).OrderBy(v => v).ToArray();
            var errors = bin.Select(p => p.ErrorCm).OrderBy(v => v).ToArray();

            xCenters.Add(Percentile(xs, 0.5));
            medians.Add(Percentile(errors, 0.5));
            p25.Add(Percentile(errors, 0.25));
            p75.Add(Percentile(errors, 0.75));
            counts.Add(bin.Count);
            lowers.Add(xs.First());
            uppers.Add(xs.Last());
        }

        return new BinnedErrorSummary
        {
            XCenter = xCenters,
            MedianError = medians,
            P25Error = p25,
            P75Error = p75,
            Counts = counts,
            BinLower = lowers,
            BinUpper = uppers
        };
    }

    private static void SaveErrorDiagnostics(
        LocalizationPipeline pipeline,
        IReadOnlyList<LocalizationRow> rows,
        string splitLabel,
        string distancePlotPath,
        string countsPlotPath,
        string distanceJsonPath,
        string countsJsonPath)
    {
        const int diagnosticBins = 20;
        var (distancePoints, countPoints) = BuildErrorDiagnosticPoints(pipeline, rows, null);
        var distanceBinned = BinDiagnostics(distancePoints, diagnosticBins);
        var countsBinned = BinDiagnostics(countPoints, diagnosticBins);

        SaveErrorVsXPlot(
            $"Localization error vs. distance ({splitLabel})",
            "Distance to detector (cm)",
            distancePlotPath,
            distanceBinned,
            null);

        SaveErrorVsXPlot(
            $"Localization error vs. total counts ({splitLabel})",
            "Total counts",
            countsPlotPath,
            countsBinned,
            null);

        File.WriteAllText(distanceJsonPath, JsonSerializer.Serialize(distanceBinned, JsonWithNamedFloats));
        File.WriteAllText(countsJsonPath, JsonSerializer.Serialize(countsBinned, JsonWithNamedFloats));
    }

    private static void SaveErrorVsXPlot(
        string title,
        string xLabel,
        string outputPath,
        BinnedErrorSummary baseline,
        BinnedErrorSummary? perturbed)
    {
        var model = new PlotModel { Title = title };
        model.Axes.Add(new LinearAxis { Position = AxisPosition.Bottom, Title = xLabel });
        model.Axes.Add(new LinearAxis { Position = AxisPosition.Left, Title = "Localization error (cm)" });

        if (baseline.XCenter.Count > 0)
        {
            var area = new AreaSeries
            {
                Title = "Baseline IQR",
                Color = OxyColor.FromAColor(60, OxyColors.SteelBlue),
                Fill = OxyColor.FromAColor(40, OxyColors.SteelBlue),
                StrokeThickness = 1
            };

            foreach (var point in baseline.XCenter.Zip(baseline.P75Error, (x, y) => new DataPoint(x, y)))
            {
                area.Points.Add(point);
            }

            for (int i = baseline.XCenter.Count - 1; i >= 0; i--)
            {
                area.Points2.Add(new DataPoint(baseline.XCenter[i], baseline.P25Error[i]));
            }

            model.Series.Add(area);

            var medianSeries = new LineSeries
            {
                Title = "Baseline median",
                StrokeThickness = 2,
                MarkerType = MarkerType.Circle,
                MarkerSize = 4,
                Color = OxyColors.SteelBlue
            };

            foreach (var point in baseline.XCenter.Zip(baseline.MedianError, (x, y) => new DataPoint(x, y)))
            {
                medianSeries.Points.Add(point);
            }

            model.Series.Add(medianSeries);
        }

        if (perturbed != null && perturbed.XCenter.Count > 0)
        {
            var area = new AreaSeries
            {
                Title = "Perturbed IQR",
                Color = OxyColor.FromAColor(60, OxyColors.IndianRed),
                Fill = OxyColor.FromAColor(40, OxyColors.IndianRed),
                StrokeThickness = 1
            };

            foreach (var point in perturbed.XCenter.Zip(perturbed.P75Error, (x, y) => new DataPoint(x, y)))
            {
                area.Points.Add(point);
            }

            for (int i = perturbed.XCenter.Count - 1; i >= 0; i--)
            {
                area.Points2.Add(new DataPoint(perturbed.XCenter[i], perturbed.P25Error[i]));
            }

            model.Series.Add(area);

            var medianSeries = new LineSeries
            {
                Title = "Perturbed median",
                StrokeThickness = 2,
                MarkerType = MarkerType.Square,
                MarkerSize = 4,
                Color = OxyColors.IndianRed
            };

            foreach (var point in perturbed.XCenter.Zip(perturbed.MedianError, (x, y) => new DataPoint(x, y)))
            {
                medianSeries.Points.Add(point);
            }

            model.Series.Add(medianSeries);
        }

        using var stream = File.Open(outputPath, FileMode.Create);
        new PngExporter { Width = 900, Height = 600 }.Export(model, stream);
    }

    private static double Euclidean(IReadOnlyList<double> predicted, IReadOnlyList<double> truth)
    {
        int dimension = Math.Min(predicted.Count, truth.Count);
        double sum = 0;
        for (int i = 0; i < dimension; i++)
        {
            sum += Math.Pow(predicted[i] - truth[i], 2);
        }

        return Math.Sqrt(sum);
    }

    private static (double First, double Second) AssignmentAwareErrors(IReadOnlyList<double> prediction, IReadOnlyList<double> truth)
    {
        if (prediction.Count < 6 || truth.Count < 6)
        {
            return (double.NaN, double.NaN);
        }

        var predA = prediction.Take(3).ToArray();
        var predB = prediction.Skip(3).Take(3).ToArray();
        var truthA = truth.Take(3).ToArray();
        var truthB = truth.Skip(3).Take(3).ToArray();

        double option1 = Euclidean(predA, truthA) + Euclidean(predB, truthB);
        double option2 = Euclidean(predA, truthB) + Euclidean(predB, truthA);

        if (option1 <= option2)
        {
            return (Euclidean(predA, truthA), Euclidean(predB, truthB));
        }

        return (Euclidean(predA, truthB), Euclidean(predB, truthA));
    }

    private static int[] BuildDeterministicPermutation(int seed)
    {
        var rng = new Random(seed);
        return Enumerable.Range(0, FeatureBuilder.ChannelCount).OrderBy(_ => rng.Next()).ToArray();
    }

    private static List<ClassificationExample> ShuffleLabels(IReadOnlyList<ClassificationExample> examples, int seed)
    {
        var labels = examples.Select(e => e.Label).ToList();
        var rng = new Random(seed);
        labels = labels.OrderBy(_ => rng.Next()).ToList();

        var shuffled = new List<ClassificationExample>(examples.Count);
        for (int i = 0; i < examples.Count; i++)
        {
            shuffled.Add(new ClassificationExample
            {
                Features = examples[i].Features,
                Label = labels[i]
            });
        }

        return shuffled;
    }

    private static double ComputeMajorityAccuracy(IEnumerable<bool> labels)
    {
        int positives = labels.Count(l => l);
        int negatives = labels.Count() - positives;
        if (positives + negatives == 0)
        {
            return double.NaN;
        }

        return Math.Max(positives, negatives) / (double)(positives + negatives);
    }

    private static void SaveRocPrComparison(
        IReadOnlyList<(bool Label, double Probability)> baseline,
        IReadOnlyList<(bool Label, double Probability)> perturbed,
        string outputPathPrefix,
        string baselineLabel,
        string perturbedLabel)
    {
        var thresholds = Enumerable.Range(0, 501).Select(i => i / 500.0).ToArray();

        var baselineRoc = thresholds.Select(t =>
            ComputeRocPoint(baseline.Select(b => b.Label).ToList(),
                            baseline.Select(b => b.Probability).ToList(), t)).ToList();

        var perturbedRoc = thresholds.Select(t =>
            ComputeRocPoint(perturbed.Select(b => b.Label).ToList(),
                            perturbed.Select(b => b.Probability).ToList(), t)).ToList();

        var baselinePr = thresholds.Select(t =>
            ComputePrPoint(baseline.Select(b => b.Label).ToList(),
                           baseline.Select(b => b.Probability).ToList(), t)).ToList();

        var perturbedPr = thresholds.Select(t =>
            ComputePrPoint(perturbed.Select(b => b.Label).ToList(),
                           perturbed.Select(b => b.Probability).ToList(), t)).ToList();

        var rocModel = CreateNormalizedModel("Classifier ROC (negative control)",
            "False Positive Rate", "True Positive Rate");

        var prModel = CreateNormalizedModel("Classifier PR (negative control)",
            "Recall", "Precision");

        AddCurve(rocModel,
            baselineRoc.Select(p => new DataPoint(p.FalsePositiveRate, p.TruePositiveRate)),
            baselineLabel, OxyColors.SteelBlue);

        AddCurve(rocModel,
            perturbedRoc.Select(p => new DataPoint(p.FalsePositiveRate, p.TruePositiveRate)),
            perturbedLabel, OxyColors.IndianRed);

        AddCurve(prModel,
            baselinePr.Select(p => new DataPoint(p.Recall, p.Precision)),
            baselineLabel, OxyColors.SteelBlue);

        AddCurve(prModel,
            perturbedPr.Select(p => new DataPoint(p.Recall, p.Precision)),
            perturbedLabel, OxyColors.IndianRed);

        using (var s = File.Open(outputPathPrefix + "_roc.png", FileMode.Create))
            new PngExporter { Width = 900, Height = 600 }.Export(rocModel, s);

        using (var s = File.Open(outputPathPrefix + "_pr.png", FileMode.Create))
            new PngExporter { Width = 900, Height = 600 }.Export(prModel, s);
    }

    private static RocPoint ComputeRocPoint(IReadOnlyList<bool> labels, IReadOnlyList<double> probabilities, double threshold)
    {
        int tp = 0, fp = 0, tn = 0, fn = 0;
        for (int i = 0; i < labels.Count; i++)
        {
            bool predicted = probabilities[i] >= threshold;
            bool actual = labels[i];
            if (predicted && actual)
            {
                tp++;
            }
            else if (predicted && !actual)
            {
                fp++;
            }
            else if (!predicted && actual)
            {
                fn++;
            }
            else
            {
                tn++;
            }
        }

        double tpr = tp + fn == 0 ? 0 : (double)tp / (tp + fn);
        double fpr = fp + tn == 0 ? 0 : (double)fp / (fp + tn);
        return new RocPoint(fpr, tpr);
    }

    private static PrPoint ComputePrPoint(IReadOnlyList<bool> labels, IReadOnlyList<double> probabilities, double threshold)
    {
        int tp = 0, fp = 0, fn = 0;
        for (int i = 0; i < labels.Count; i++)
        {
            bool predicted = probabilities[i] >= threshold;
            bool actual = labels[i];
            if (predicted && actual)
            {
                tp++;
            }
            else if (predicted && !actual)
            {
                fp++;
            }
            else if (!predicted && actual)
            {
                fn++;
            }
        }

        double precision = tp + fp == 0 ? 1 : (double)tp / (tp + fp);
        double recall = tp + fn == 0 ? 0 : (double)tp / (tp + fn);
        return new PrPoint(recall, precision);
    }

    private static PlotModel CreateNormalizedModel(string title, string xLabel, string yLabel)
    {
        var model = new PlotModel { Title = title };
        model.Axes.Add(new LinearAxis { Position = AxisPosition.Bottom, Minimum = 0, Maximum = 1, Title = xLabel });
        model.Axes.Add(new LinearAxis { Position = AxisPosition.Left, Minimum = 0, Maximum = 1, Title = yLabel });
        return model;
    }

    private static void AddCurve(PlotModel model, IEnumerable<DataPoint> points, string title, OxyColor color)
    {
        var series = new LineSeries { Title = title, StrokeThickness = 2, Color = color };
        foreach (var point in points)
        {
            series.Points.Add(point);
        }

        model.Series.Add(series);
    }

    private static void SaveCdfComparison(
        IReadOnlyList<double> baselineErrors,
        IReadOnlyList<double> perturbedErrors,
        string outputPath,
        string title)
    {
        var model = CreateCdfModel(title, "Error (cm)", "CDF");

        void AddCdf(IReadOnlyList<double> errors, string label, OxyColor color)
        {
            if (errors.Count == 0)
            {
                return;
            }

            var sorted = errors.OrderBy(e => e).ToArray();
            var (xs, ys) = BuildCdf(sorted);
            var series = new LineSeries { Title = label, StrokeThickness = 2, Color = color };
            for (int i = 0; i < xs.Length; i++)
            {
                series.Points.Add(new DataPoint(xs[i], ys[i]));
            }

            model.Series.Add(series);
        }

        AddCdf(baselineErrors, "Baseline", OxyColors.SteelBlue);
        AddCdf(perturbedErrors, "Perturbed", OxyColors.IndianRed);

        using var stream = File.Open(outputPath, FileMode.Create);
        new PngExporter { Width = 900, Height = 600 }.Export(model, stream);
    }

    private static PlotModel CreateCdfModel(string title, string xLabel, string yLabel)
    {
        var model = new PlotModel
        {
            Title = title
        };

        model.Axes.Add(new LinearAxis
        {
            Position = AxisPosition.Bottom,
            Title = xLabel,
            Minimum = 0,
            MajorGridlineStyle = LineStyle.Solid,
            MinorGridlineStyle = LineStyle.Dot
        });

        model.Axes.Add(new LinearAxis
        {
            Position = AxisPosition.Left,
            Title = yLabel,
            Minimum = 0,
            Maximum = 1,
            MajorGridlineStyle = LineStyle.Solid,
            MinorGridlineStyle = LineStyle.Dot
        });

        return model;
    }

    private static (double[] Xs, double[] Ys) BuildCdf(IReadOnlyList<double> sorted)
    {
        var xs = new double[sorted.Count];
        var ys = new double[sorted.Count];
        for (int i = 0; i < sorted.Count; i++)
        {
            xs[i] = sorted[i];
            ys[i] = (i + 1) / (double)sorted.Count;
        }

        return (xs, ys);
    }

    private static void SaveRoutingComparison(RoutingBreakdown baseline, RoutingBreakdown perturbed, string outputPath)
    {
        var model = new PlotModel { Title = "Routing breakdown (baseline vs perturbed)" };

        // Use category names on the bottom axis, but plot using numeric X indices (0..3).
        var categoryAxis = new CategoryAxis
        {
            Position = AxisPosition.Bottom
        };
        categoryAxis.Labels.AddRange(new[] { "Single", "Dual", "Centroid", "Unknown" });
        model.Axes.Add(categoryAxis);

        model.Axes.Add(new LinearAxis
        {
            Position = AxisPosition.Left,
            Minimum = 0,
            Maximum = 1,
            Title = "Fraction"
        });

        var baselineSeries = new LineSeries
        {
            Title = "Baseline",
            StrokeThickness = 2,
            MarkerType = MarkerType.Circle,
            MarkerSize = 4,
            Color = OxyColors.SteelBlue
        };

        var perturbedSeries = new LineSeries
        {
            Title = "Perturbed",
            StrokeThickness = 2,
            MarkerType = MarkerType.Square,
            MarkerSize = 4,
            Color = OxyColors.IndianRed
        };

        baselineSeries.Points.Add(new DataPoint(0, baseline.SingleFraction));
        baselineSeries.Points.Add(new DataPoint(1, baseline.DualFraction));
        baselineSeries.Points.Add(new DataPoint(2, baseline.CentroidFraction));
        baselineSeries.Points.Add(new DataPoint(3, baseline.UnknownFraction));

        perturbedSeries.Points.Add(new DataPoint(0, perturbed.SingleFraction));
        perturbedSeries.Points.Add(new DataPoint(1, perturbed.DualFraction));
        perturbedSeries.Points.Add(new DataPoint(2, perturbed.CentroidFraction));
        perturbedSeries.Points.Add(new DataPoint(3, perturbed.UnknownFraction));

        model.Series.Add(baselineSeries);
        model.Series.Add(perturbedSeries);

        using var stream = File.Open(outputPath, FileMode.Create);
        new PngExporter { Width = 900, Height = 600 }.Export(model, stream);
    }

    private sealed record LocalizationEvaluationResult(List<double> SingleErrors, List<double> DualErrors, RoutingBreakdown Routing);

    private sealed record RoutingBreakdown
    {
        public double SingleFraction { get; init; }
        public double DualFraction { get; init; }
        public double CentroidFraction { get; init; }
        public double UnknownFraction { get; init; }
        public int Total { get; init; }
    }

    private sealed record LocalizationErrorSummary
    {
        public required double Mean { get; init; }
        public required double Median { get; init; }
        public required double Rmse { get; init; }
        public required int Count { get; init; }
    }

    private sealed record BinnedErrorSummary
    {
        public required List<double> XCenter { get; init; }
        public required List<double> MedianError { get; init; }
        public required List<double> P25Error { get; init; }
        public required List<double> P75Error { get; init; }
        public required List<int> Counts { get; init; }
        public required List<double> BinLower { get; init; }
        public required List<double> BinUpper { get; init; }
    }

    private sealed record RocPoint(double FalsePositiveRate, double TruePositiveRate);
    private sealed record PrPoint(double Recall, double Precision);

    private sealed record ConfusionCounts
    {
        public int TruePositives { get; init; }
        public int FalsePositives { get; init; }
        public int TrueNegatives { get; init; }
        public int FalseNegatives { get; init; }
    }

    private sealed record ClassificationMetricsSummary
    {
        public double Accuracy { get; init; }
        public double Precision { get; init; }
        public double Recall { get; init; }
        public double F1 { get; init; }
        public double RocAuc { get; init; }
        public double PrAuc { get; init; }
        public ConfusionCounts Confusion { get; init; } = new();
    }

    private sealed record NegativeControlSplitResult
    {
        public required ClassificationMetricsSummary BaselineClassifier { get; init; }
        public required ClassificationMetricsSummary PerturbedClassifier { get; init; }
        public required LocalizationErrorSummary BaselineSingle { get; init; }
        public required LocalizationErrorSummary PerturbedSingle { get; init; }
        public required LocalizationErrorSummary BaselineDual { get; init; }
        public required LocalizationErrorSummary PerturbedDual { get; init; }
        public required RoutingBreakdown BaselineRouting { get; init; }
        public required RoutingBreakdown PerturbedRouting { get; init; }
    }

    private sealed record NegativeControlChannelPermutationSummary
    {
        public required int Seed { get; init; }
        public required IReadOnlyList<int> Permutation { get; init; }
        public required NegativeControlSplitResult RandomHoldout { get; init; }
        public required NegativeControlSplitResult GroupedHoldout { get; init; }
    }

    private sealed record NegativeControlLabelShuffleSplit
    {
        public required double MajorityBaselineAccuracy { get; init; }
        public required ClassificationMetricsSummary Metrics { get; init; }
    }

    private sealed record NegativeControlLabelShuffleSummary
    {
        public required int Seed { get; init; }
        public required NegativeControlLabelShuffleSplit RandomHoldout { get; init; }
        public required NegativeControlLabelShuffleSplit GroupedHoldout { get; init; }
    }

    private static double TrainWithGroupedHoldout(ModelTrainer trainer, IReadOnlyList<LocalizationRow> rows, FeatureBuilder featureBuilder, IReadOnlyList<string> targetNames, Func<LocalizationRow, double[]> targetSelector)
    {
        if (rows.Count == 0)
        {
            return double.NaN;
        }

        var split = Grouping.GroupSplit(rows, 0.25, seed: 99);
        if (split.Train.Count == 0 || split.Holdout.Count == 0)
        {
            return double.NaN;
        }

        var trainFeatures = new List<float[]>();
        var trainTargets = new List<double[]>();
        var testFeatures = new List<float[]>();
        var testTargets = new List<double[]>();

        foreach (var row in split.Train)
        {
            trainFeatures.Add(BuildClassificationExample(featureBuilder, row).Features);
            trainTargets.Add(targetSelector(row));
        }

        foreach (var row in split.Holdout)
        {
            testFeatures.Add(BuildClassificationExample(featureBuilder, row).Features);
            testTargets.Add(targetSelector(row));
        }

        var regressor = trainer.TrainMultiRegressor(trainFeatures, trainTargets, targetNames);
        var r2 = ComputeR2(regressor, testFeatures, testTargets);
        return r2;
    }

    private static (string DataDir, string OutputDir, double? DurationOverride, bool UseGroupedSplit, bool ValidateOod, double OodFaultFraction, int OodSeed, bool RunNegativeControls, int NegativeControlSeed, bool RunPermutationControl, bool RunLabelShuffleControl) ParseArgs(string[] args)
    {
        string dataDir = ".";
        string outputDir = "artifacts";
        double? duration = null;
        bool useGroupedSplit = false;
        bool validateOod = true;
        double oodFaultFraction = 0.5;
        int oodSeed = 123;
        bool runNegativeControls = false;
        int negativeControlSeed = 2024;
        bool runPermutationControl = false;
        bool runLabelShuffleControl = false;

        foreach (var arg in args)
        {
            if (arg.StartsWith("--data-dir="))
            {
                dataDir = arg.Substring("--data-dir=".Length);
            }
            else if (arg.StartsWith("--output-dir="))
            {
                outputDir = arg.Substring("--output-dir=".Length);
            }
            else if (arg.StartsWith("--duration="))
            {
                duration = double.Parse(arg.Substring("--duration=".Length), CultureInfo.InvariantCulture);
            }
            else if (arg.StartsWith("--use-grouped-split="))
            {
                useGroupedSplit = bool.Parse(arg.Substring("--use-grouped-split=".Length));
            }
            else if (arg.StartsWith("--validate-ood="))
            {
                validateOod = bool.Parse(arg.Substring("--validate-ood=".Length));
            }
            else if (arg.StartsWith("--ood-fault-fraction="))
            {
                oodFaultFraction = double.Parse(arg.Substring("--ood-fault-fraction=".Length), CultureInfo.InvariantCulture);
            }
            else if (arg.StartsWith("--ood-seed="))
            {
                oodSeed = int.Parse(arg.Substring("--ood-seed=".Length), CultureInfo.InvariantCulture);
            }
            else if (arg.Equals("--negative-controls", StringComparison.OrdinalIgnoreCase))
            {
                runNegativeControls = true;
            }
            else if (arg.StartsWith("--negctrl-seed="))
            {
                negativeControlSeed = int.Parse(arg.Substring("--negctrl-seed=".Length), CultureInfo.InvariantCulture);
            }
            else if (arg.Equals("--negctrl-permute-channels", StringComparison.OrdinalIgnoreCase))
            {
                runPermutationControl = true;
            }
            else if (arg.Equals("--negctrl-shuffle-labels", StringComparison.OrdinalIgnoreCase))
            {
                runLabelShuffleControl = true;
            }
        }

        runPermutationControl |= runNegativeControls;
        runLabelShuffleControl |= runNegativeControls;

        return (dataDir, outputDir, duration, useGroupedSplit, validateOod, oodFaultFraction, oodSeed, runNegativeControls, negativeControlSeed, runPermutationControl, runLabelShuffleControl);
    }

    private static List<LocalizationRow> LoadSingleGroups(string dataDir, double? durationOverride)
    {
        var rows = new List<LocalizationRow>();
        foreach (var group in SingleGroups)
        {
            var files = FindFilesByPrefix(dataDir, group);
            if (files.Count == 0)
            {
                Console.WriteLine($"Warning: missing single-source group {group}");
                continue;
            }

            foreach (var file in files)
            {
                rows.AddRange(DatasetLoader.LoadSingleSource(file, durationOverride));
            }
        }

        return rows;
    }

    private static List<LocalizationRow> LoadDualGroups(string dataDir, double? durationOverride)
    {
        var list = new List<LocalizationRow>();
        foreach (var group in DualGroups)
        {
            var countFiles = FindFilesByPrefix(dataDir, group);
            if (countFiles.Count == 0)
            {
                Console.WriteLine($"Warning: missing dual-source group {group}");
                continue;
            }

            if (!PairMetadataGroups.TryGetValue(group, out var metadataPrefix))
            {
                Console.WriteLine($"Warning: missing pair metadata mapping for group {group}");
                continue;
            }

            var metadataFiles = FindFilesByPrefix(dataDir, metadataPrefix);
            if (metadataFiles.Count == 0)
            {
                Console.WriteLine($"Warning: missing pair metadata group {metadataPrefix} for dual group {group}");
                continue;
            }

            if (metadataFiles.Count > 1)
            {
                Console.WriteLine($"Warning: multiple metadata files found for {metadataPrefix}; using {metadataFiles[0]}");
            }

            foreach (var countFile in countFiles)
            {
                list.AddRange(DatasetLoader.LoadDualSource(countFile, metadataFiles[0], durationOverride));
            }
        }

        return list;
    }

    private static List<string> FindFilesByPrefix(string dataDir, string prefix)
    {
        var files = Directory.EnumerateFiles(dataDir)
            .Where(path =>
            {
                var extension = Path.GetExtension(path);
                if (!string.Equals(extension, ".csv", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(extension, ".xlsx", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                var name = Path.GetFileNameWithoutExtension(path);
                return name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
            })
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return files;
    }

    private static RegressionModelGroup TrainFallbackRegressor(ModelTrainer trainer, FeatureBuilder featureBuilder, double? durationOverride, IReadOnlyList<string> targetNames)
    {
        var channels = new double[FeatureBuilder.ChannelCount];
        var duration = durationOverride ?? 0;
        var features = featureBuilder.BuildFeatures(channels, duration).FeatureVector.Select(f => (float)f).ToArray();
        var targets = new double[targetNames.Count];
        return trainer.TrainMultiRegressor(new[] { features }, new[] { targets }, targetNames);
    }
}
