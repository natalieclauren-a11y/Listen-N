using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.ML;
using Microsoft.ML.Trainers.FastTree;
using Localization.ML;

namespace Localization.Train;

internal static class Program
{
    private static readonly string[] SingleFiles =
    {
        "Cf_30_Second_LMX.csv",
        "Single_60_Second_Cf.csv"
    };

    private static readonly string[] DualFiles =
    {
        "Dual_Cf_30_Second.csv",
        "Dual_Cf_60_Second_LMX.csv"
    };

    public static void Main(string[] args)
    {
        var (dataDir, outputDir, durationOverride) = ParseArgs(args);
        Directory.CreateDirectory(outputDir);

        var trainer = new ModelTrainer();
        var featureBuilder = trainer.FeatureBuilder;

        var singleRows = LoadRows(dataDir, SingleFiles, false, durationOverride);
        var dualRows = LoadRows(dataDir, DualFiles, true, durationOverride);
        var allRows = singleRows.Concat(dualRows).ToList();

        Console.WriteLine($"Loaded {singleRows.Count} single-source rows and {dualRows.Count} dual-source rows");

        var classificationExamples = allRows.Select(r =>
        {
            var features = featureBuilder.BuildFeatures(r.Channels, r.DurationSeconds).FeatureVector;
            return new ClassificationExample { Label = r.IsDual, Features = features.Select(f => (float)f).ToArray() };
        }).ToList();

        var (classifierHoldoutModel, metrics, importances) = trainer.TrainClassifier(classificationExamples);
        var cv = trainer.CrossValidateClassifier(classificationExamples);
        var randomCheck = trainer.RandomLabelSanityCheck(classificationExamples);

        Console.WriteLine("Classifier holdout metrics:");
        Console.WriteLine($"  Accuracy: {metrics.Accuracy:F3}");
        Console.WriteLine($"  Precision: {metrics.PositivePrecision:F3}");
        Console.WriteLine($"  Recall: {metrics.PositiveRecall:F3}");
        Console.WriteLine($"  F1: {metrics.F1Score:F3}");
        Console.WriteLine("Confusion matrix (rows=actual, cols=predicted):");
        var matrix = metrics.ConfusionMatrix;
        Console.WriteLine($"  TN={matrix.Counts[0][0]}, FP={matrix.Counts[0][1]}");
        Console.WriteLine($"  FN={matrix.Counts[1][0]}, TP={matrix.Counts[1][1]}");

        Console.WriteLine($"Cross-validation accuracy: mean={cv.Mean:F3}, std={cv.Std:F3}");
        Console.WriteLine($"Random-label sanity accuracy: {randomCheck:F3}");
        Console.WriteLine("Top feature importances (AUC gain):");
        foreach (var item in importances.OrderByDescending(i => i.Gain).Take(10))
        {
            Console.WriteLine($"  {item.Feature}: {item.Gain:F5}");
        }

        // Train classifier on full data for saving
        var classifierPipeline = trainer.MlContext.BinaryClassification.Trainers.FastForest(
            new Microsoft.ML.Trainers.FastTree.FastForestBinaryTrainer.Options
            {
                NumberOfTrees = 200,
                NumberOfLeaves = 64,
                LabelColumnName = nameof(ClassificationExample.Label),
                FeatureColumnName = nameof(ClassificationExample.Features)
            });

        var classifierFull = classifierPipeline.Fit(
            trainer.MlContext.Data.LoadFromEnumerable(classificationExamples));


        // Regression datasets
        var singleFeatures = singleRows.Select(r => featureBuilder.BuildFeatures(r.Channels, r.DurationSeconds).FeatureVector.Select(f => (float)f).ToArray()).ToList();
        var singleTargets = singleRows.Select(r => r.SingleCoordinates!).ToList();
        var dualFeatures = dualRows.Select(r => featureBuilder.BuildFeatures(r.Channels, r.DurationSeconds).FeatureVector.Select(f => (float)f).ToArray()).ToList();
        var dualTargets = dualRows.Select(r => r.DualCoordinates!).ToList();

        double singleR2 = TrainWithHoldout(trainer, singleFeatures, singleTargets, new[] { "x", "y", "z" });
        double dualR2 = TrainWithHoldout(trainer, dualFeatures, dualTargets, new[] { "x1", "y1", "z1", "x2", "y2", "z2" });

        Console.WriteLine($"Single-source regressor R^2 (mean): {singleR2:F3}");
        Console.WriteLine($"Dual-source regressor R^2 (mean): {dualR2:F3}");

        // Fit final regressors on all data
        var singleRegressor = trainer.TrainMultiRegressor(singleFeatures, singleTargets, new[] { "x", "y", "z" });
        var dualRegressor = trainer.TrainMultiRegressor(dualFeatures, dualTargets, new[] { "x1", "y1", "z1", "x2", "y2", "z2" });

        // OOD scoring
        var oodFeatures = classificationExamples.Select(c => ExtractOodVector(c.Features)).ToList();
        var mahalanobis = MahalanobisScorer.FromSamples(oodFeatures.Select(v => v.Select(x => (double)x).ToArray()));
        var oodDistances = oodFeatures.Select(v => mahalanobis.Score(v.Select(x => (double)x).ToArray())).ToList();
        double oodMean = oodDistances.Average();
        double oodStd = Math.Sqrt(oodDistances.Average(d => Math.Pow(d - oodMean, 2)));
        double oodThreshold = oodMean + 3 * oodStd;

        var summary = new TrainingSummary
        {
            HoldoutAccuracy = metrics.Accuracy,
            HoldoutPrecision = metrics.PositivePrecision,
            HoldoutRecall = metrics.PositiveRecall,
            HoldoutF1 = metrics.F1Score,
            CrossValidationAccuracyMean = cv.Mean,
            CrossValidationAccuracyStd = cv.Std,
            RandomLabelAccuracy = randomCheck,
            SingleRegressorR2 = singleR2,
            DualRegressorR2 = dualR2,
            FeatureImportance = importances
        };

        var config = new PipelineConfiguration
        {
            Epsilon = featureBuilder.Epsilon,
            OutOfDistributionThreshold = oodThreshold,
            MinimumSeparationCm = 8,
            StrictProbability = 0.98,
            FeatureNames = featureBuilder.FeatureNames,
            DipolePositions = featureBuilder.DipolePositions,
            TrainingSummary = summary
        };

        var pipeline = new LocalizationPipeline(trainer.MlContext, featureBuilder, classifierFull, singleRegressor, dualRegressor, mahalanobis, config);
        pipeline.Save(outputDir);

        File.WriteAllText(Path.Combine(outputDir, "training_summary.json"), JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Artifacts saved to {outputDir}");
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

    private static (string DataDir, string OutputDir, double? DurationOverride) ParseArgs(string[] args)
    {
        string dataDir = ".";
        string outputDir = "artifacts";
        double? duration = null;

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
        }

        return (dataDir, outputDir, duration);
    }

    private static List<LocalizationRow> LoadRows(string dataDir, IEnumerable<string> files, bool isDual, double? durationOverride)
    {
        var list = new List<LocalizationRow>();
        foreach (var file in files)
        {
            var path = Path.Combine(dataDir, file);
            if (!File.Exists(path))
            {
                Console.WriteLine($"Warning: file {path} not found, skipping");
                continue;
            }

            var rows = isDual ? DatasetLoader.LoadDualSource(path, durationOverride) : DatasetLoader.LoadSingleSource(path, durationOverride);
            list.AddRange(rows);
        }

        return list;
    }
}
