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

    public static void Main(string[] args)
    {
        var (dataDir, outputDir, durationOverride) = ParseArgs(args);
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
            return new ClassificationExample { Label = r.IsDual, Features = features.Select(f => (float)f).ToArray() };
        }).ToList();

        if (classificationExamples.Count == 0)
        {
            throw new InvalidOperationException("No training rows loaded. Check --data-dir and available dataset groups.");
        }

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

        Console.WriteLine($"Single-source regressor R^2 (mean): {singleR2:F3}");
        Console.WriteLine($"Dual-source regressor R^2 (mean): {dualR2:F3}");

        // Fit final regressors on all data
        var singleRegressor = singleFeatures.Count == 0
            ? TrainFallbackRegressor(trainer, featureBuilder, durationOverride, new[] { "x", "y", "z" })
            : trainer.TrainMultiRegressor(singleFeatures, singleTargets, new[] { "x", "y", "z" });
        var dualRegressor = dualFeatures.Count == 0
            ? TrainFallbackRegressor(trainer, featureBuilder, durationOverride, new[] { "x1", "y1", "z1", "x2", "y2", "z2" })
            : trainer.TrainMultiRegressor(dualFeatures, dualTargets, new[] { "x1", "y1", "z1", "x2", "y2", "z2" });

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
