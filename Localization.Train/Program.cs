using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
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

    private sealed record CoverageSummary(int Single, int Dual, int Centroid, int Unknown)
    {
        public int Total => Single + Dual + Centroid + Unknown;

        public double SingleFraction => Fraction(Single);

        public double DualFraction => Fraction(Dual);

        public double CentroidFraction => Fraction(Centroid);

        public double UnknownFraction => Fraction(Unknown);

        private double Fraction(int count) => Total == 0 ? double.NaN : (double)count / Total;
    }

    private sealed record NormalizationSelection(LocalizationRow Low, LocalizationRow High, (double X, double Y, double Z) Position, (int X, int Y, int Z) RoundedKey, double LowTotal, double HighTotal)
    {
        public double Ratio => HighTotal / LowTotal;
    }

    private static int IndexOf(IReadOnlyList<string> list, string value)
    {
        for (int i = 0; i < list.Count; i++)
        {
            if (string.Equals(list[i], value, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    public static void Main(string[] args)
    {
        var (dataDir, outputDir, durationOverride, useGroupedSplit, validateOod, oodFaultFraction, oodSeed, runNegativeControls, negativeControlSeed, runPermutationControl, runLabelShuffleControl, emitFeatureSchemaTex, texOutPath, texCaption, texLabel, emitLockedSchemaTex, lockedTexOutPath, lockedTexCaption, lockedTexLabel, emitNormalizationFigure, normFigOutPath, normFigRegime, normFigRoundCm, normFigMinCountRatio, normFigMaxExamples, normFigTitle, emitDescriptorFigure, descFigOutPath, descFigBins, descFigRegime, descFigTitle, emitOodFigure, oodFigOutPath, oodFigTitle, oodFigBins, oodFigMaxPoints, oodFigThresholdMode, oodFigThresholdK, oodFigRegime, emitClassifierFigure, clfFigOutPath, clfFigTitle, clfFigMaxPoints, clfFigThreshold, clfFigRegime, emitSingleErrorFigure, singleErrFigOut, singleErrFigTitle, singleErrFigRegime, singleErrMaxPoints, emitDualErrorFigure, dualErrFigOut, dualErrFigTitle, dualErrFigRegime, dualErrErrorMetric, dualErrMaxPoints) = ParseArgs(args);
        Directory.CreateDirectory(outputDir);

        if (emitLockedSchemaTex)
        {
            var builder = new FeatureBuilder();

            var lockedConfig = new PipelineConfiguration
            {
                Epsilon = builder.Epsilon,
                OutOfDistributionThreshold = 0,
                ClassifierThreshold = 0.5,
                MinimumSeparationCm = 8,
                StrictProbability = 0.98,
                FeatureNames = builder.FeatureNames,
                FeatureColumns = builder.FeatureNames,
                DipolePositions = builder.DipolePositions,
                ClassifierTrainer = "FastForestBinary",
                RegressorTrainers = new[] { "FastForestRegression", "FastForestRegression" }
            };

            var lockedSchemaStamp = SchemaStampBuilder.Build(
                lockedConfig,
                builder,
                lockedConfig.ClassifierTrainer!,
                lockedConfig.RegressorTrainers!);

            lockedConfig.SchemaVersion = SchemaStampBuilder.DefaultSchemaVersion;
            lockedConfig.SchemaHash = SchemaStampBuilder.ComputeHash(lockedSchemaStamp);

            string outputPath = lockedTexOutPath ?? Path.Combine(outputDir, "locked_feature_schema.tex");
            string tex = LockedSchemaTexWriter.BuildLockedFeatureSchemaTableTex(builder, lockedTexCaption, lockedTexLabel);
            LockedSchemaTexWriter.WriteTo(outputPath, tex);

            Console.WriteLine($"Locked schema LaTeX written to {outputPath}");
            Console.WriteLine($"SchemaVersion={lockedConfig.SchemaVersion}, SchemaHash={lockedConfig.SchemaHash}");
            Console.WriteLine($"Feature count: {builder.FeatureNames.Count}");
            return;
        }

        if (emitFeatureSchemaTex)
        {
            var builder = new FeatureBuilder();
            string outputPath = texOutPath ?? Path.Combine(outputDir, "raw_feature_schema.tex");
            string tex = FeatureSchemaTexWriter.BuildRawFeatureSchemaTableTex(builder, texCaption, texLabel);
            FeatureSchemaTexWriter.WriteTo(outputPath, tex);
            Console.WriteLine($"Feature schema LaTeX written to {outputPath}");
            return;
        }

        if (emitNormalizationFigure)
        {
            var singleRowsForNorm = LoadSingleGroups(dataDir, durationOverride);
            var dualRowsForNorm = LoadDualGroups(dataDir, durationOverride);

            var selection = FindNormalizationExamples(singleRowsForNorm, dualRowsForNorm, normFigRegime, normFigRoundCm, normFigMinCountRatio, normFigMaxExamples);
            if (selection == null)
            {
                Console.WriteLine($"Warning: no position found with at least two rows and high/low ratio >= {normFigMinCountRatio:F2}.");
                Environment.Exit(1);
            }

            string outputPath = normFigOutPath ?? Path.Combine(outputDir, "normalization_effect.png");
            string subtitle = $"Pos (cm): x={selection.Position.X:F2}, y={selection.Position.Y:F2}, z={selection.Position.Z:F2} | high/low={selection.Ratio:F2}x";
            NormalizationEffectFigureWriter.Write(outputPath, selection.Low, selection.High, normFigTitle, subtitle);

            Console.WriteLine($"Normalization figure written to {outputPath}");
            Console.WriteLine($"Regime: {normFigRegime}");
            Console.WriteLine($"Rounded position key (cm, round={normFigRoundCm:F2}): x={selection.RoundedKey.X * normFigRoundCm:F2}, y={selection.RoundedKey.Y * normFigRoundCm:F2}, z={selection.RoundedKey.Z * normFigRoundCm:F2}");
            Console.WriteLine($"Selected position (cm): x={selection.Position.X:F2}, y={selection.Position.Y:F2}, z={selection.Position.Z:F2}");
            Console.WriteLine($"Low total={selection.LowTotal:F0}, High total={selection.HighTotal:F0}, ratio={selection.Ratio:F2}x");
            return;
        }

        if (emitDescriptorFigure)
        {
            var builder = new FeatureBuilder();
            int entropyIdx = IndexOf(builder.FeatureNames, "Entropy");
            int giniIdx = IndexOf(builder.FeatureNames, "Gini");
            int anisIdx = IndexOf(builder.FeatureNames, "Anisotropy");
            int dipoleIdx = IndexOf(builder.FeatureNames, "DipoleMagnitude");

            if (entropyIdx < 0 || giniIdx < 0 || anisIdx < 0 || dipoleIdx < 0)
            {
                throw new InvalidOperationException("One or more descriptor features (Entropy, Gini, Anisotropy, DipoleMagnitude) were not found in FeatureNames.");
            }

            var singleRowsForDesc = LoadSingleGroups(dataDir, durationOverride);
            var dualRowsForDesc = LoadDualGroups(dataDir, durationOverride);

            var filteredSingle = descFigRegime.Equals("dual", StringComparison.OrdinalIgnoreCase) ? new List<LocalizationRow>() : singleRowsForDesc;
            var filteredDual = descFigRegime.Equals("single", StringComparison.OrdinalIgnoreCase) ? new List<LocalizationRow>() : dualRowsForDesc;

            var entropySingle = new List<double>();
            var entropyDual = new List<double>();
            var giniSingle = new List<double>();
            var giniDual = new List<double>();
            var anisSingle = new List<double>();
            var anisDual = new List<double>();
            var dipoleSingle = new List<double>();
            var dipoleDual = new List<double>();

            void AddDescriptors(IEnumerable<LocalizationRow> rows, List<double> entropyDest, List<double> giniDest, List<double> anisDest, List<double> dipoleDest)
            {
                foreach (var row in rows)
                {
                    var features = builder.BuildFeatures(row.Channels, row.DurationSeconds).FeatureVector;
                    entropyDest.Add(features[entropyIdx]);
                    giniDest.Add(features[giniIdx]);
                    anisDest.Add(features[anisIdx]);
                    dipoleDest.Add(features[dipoleIdx]);
                }
            }

            AddDescriptors(filteredSingle, entropySingle, giniSingle, anisSingle, dipoleSingle);
            AddDescriptors(filteredDual, entropyDual, giniDual, anisDual, dipoleDual);

            string outputPath = descFigOutPath ?? Path.Combine(outputDir, "descriptor_distributions.png");
            string subtitle = $"Descriptors from normalized channel fractions (N={filteredSingle.Count + filteredDual.Count}, single={filteredSingle.Count}, dual={filteredDual.Count})";

            DescriptorDistributionFigureWriter.Write(
                outputPath,
                entropySingle, entropyDual,
                giniSingle, giniDual,
                anisSingle, anisDual,
                dipoleSingle, dipoleDual,
                descFigBins,
                descFigTitle,
                subtitle);

            Console.WriteLine($"Descriptor figure written to {outputPath}");
            Console.WriteLine($"N_total={filteredSingle.Count + filteredDual.Count}, N_single={filteredSingle.Count}, N_dual={filteredDual.Count}");
            Console.WriteLine($"Bins={descFigBins}");
            return;
        }

        if (emitSingleErrorFigure)
        {
            GenerateSingleErrorFigure(
                dataDir,
                outputDir,
                durationOverride,
                singleErrFigOut,
                singleErrFigTitle,
                singleErrFigRegime,
                singleErrMaxPoints,
                useGroupedSplit);
            return;
        }
        if (emitDualErrorFigure)
        {
            GenerateDualErrorFigure(
                dataDir,
                outputDir,
                durationOverride,
                dualErrFigOut,
                dualErrFigTitle,
                dualErrFigRegime,
                dualErrErrorMetric,
                dualErrMaxPoints,
                useGroupedSplit);
            return;
        }
        if (emitOodFigure)
        {
            GenerateOodMahalanobisFigure(
                dataDir,
                outputDir,
                durationOverride,
                oodFigOutPath,
                oodFigTitle,
                oodFigBins,
                oodFigMaxPoints,
                oodFigThresholdMode,
                oodFigThresholdK,
                oodFigRegime,
                useGroupedSplit);
            return;
        }

        if (emitClassifierFigure)
        {
            GenerateClassifierFigure(
                dataDir,
                outputDir,
                durationOverride,
                clfFigOutPath,
                clfFigTitle,
                clfFigMaxPoints,
                clfFigThreshold,
                clfFigRegime,
                useGroupedSplit);
            return;
        }
      if (emitNormalizationFigure)
                {
                        var singleRowsForNorm = LoadSingleGroups(dataDir, durationOverride);
                        var dualRowsForNorm = LoadDualGroups(dataDir, durationOverride);
		
			var selection = FindNormalizationExamples(singleRowsForNorm, dualRowsForNorm, normFigRegime, normFigRoundCm, normFigMinCountRatio, normFigMaxExamples);
			if (selection == null)
			{
				Console.WriteLine($"Warning: no position found with at least two rows and high/low ratio >= {normFigMinCountRatio:F2}.");
				Environment.Exit(1);
			}
		
			string outputPath = normFigOutPath ?? Path.Combine(outputDir, "normalization_effect.png");
			string subtitle = $"Pos (cm): x={selection.Position.X:F2}, y={selection.Position.Y:F2}, z={selection.Position.Z:F2} | high/low={selection.Ratio:F2}x";
			NormalizationEffectFigureWriter.Write(outputPath, selection.Low, selection.High, normFigTitle, subtitle);
		
			Console.WriteLine($"Normalization figure written to {outputPath}");
			Console.WriteLine($"Regime: {normFigRegime}");
			Console.WriteLine($"Rounded position key (cm, round={normFigRoundCm:F2}): x={selection.RoundedKey.X * normFigRoundCm:F2}, y={selection.RoundedKey.Y * normFigRoundCm:F2}, z={selection.RoundedKey.Z * normFigRoundCm:F2}");
			Console.WriteLine($"Selected position (cm): x={selection.Position.X:F2}, y={selection.Position.Y:F2}, z={selection.Position.Z:F2}");
			Console.WriteLine($"Low total={selection.LowTotal:F0}, High total={selection.HighTotal:F0}, ratio={selection.Ratio:F2}x");
			return;
		}


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
            ClassifierThreshold = 0.5,
            MinimumSeparationCm = 8,
            StrictProbability = 0.98,
            FeatureNames = featureBuilder.FeatureNames,
            FeatureColumns = featureBuilder.FeatureNames,
            DipolePositions = featureBuilder.DipolePositions,
            ClassifierTrainer = "FastForestBinary",
            RegressorTrainers = new[] { "FastForestRegression", "FastForestRegression" },
            TrainingSummary = summary,
            Calibration = calibrationReport
        };

        var schemaStamp = SchemaStampBuilder.Build(config, featureBuilder, config.ClassifierTrainer!, config.RegressorTrainers!);
        config.SchemaVersion = SchemaStampBuilder.DefaultSchemaVersion;
        config.SchemaHash = SchemaStampBuilder.ComputeHash(schemaStamp);

        var pipeline = new LocalizationPipeline(trainer.MlContext, featureBuilder, classifierHoldoutModel, singleRegressor, dualRegressor, mahalanobis, config);
        pipeline.Save(outputDir);

        File.WriteAllText(Path.Combine(outputDir, "training_summary.json"), JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Artifacts saved to {outputDir}");
        Console.WriteLine($"Artifact schema: SchemaVersion={config.SchemaVersion}, SchemaHash={config.SchemaHash}");

        var randomHoldoutRows = split.Test.Select(t => t.Row).ToList();
        var randomCoverage = ComputeCoverage(pipeline, randomHoldoutRows);
        SaveErrorDiagnostics(
            pipeline,
            randomHoldoutRows,
            "Random holdout",
            Path.Combine(outputDir, "error_vs_distance_random.png"),
            Path.Combine(outputDir, "error_vs_counts_random.png"),
            Path.Combine(outputDir, "error_vs_distance_random.json"),
            Path.Combine(outputDir, "error_vs_counts_random.json"));

        SaveCoverageArtifacts(
            randomCoverage,
            config,
            Path.Combine(outputDir, "coverage_random.json"),
            Path.Combine(outputDir, "coverage_random.png"),
            "Coverage (random holdout)");

        if (groupedSplit is { Holdout: { } groupedHoldout } && groupedHoldout.Count > 0)
        {
            var groupedCoverage = ComputeCoverage(pipeline, groupedHoldout);
            SaveErrorDiagnostics(
                pipeline,
                groupedHoldout,
                "Grouped holdout",
                Path.Combine(outputDir, "error_vs_distance_grouped.png"),
                Path.Combine(outputDir, "error_vs_counts_grouped.png"),
                Path.Combine(outputDir, "error_vs_distance_grouped.json"),
                Path.Combine(outputDir, "error_vs_counts_grouped.json"));

            SaveCoverageArtifacts(
                groupedCoverage,
                config,
                Path.Combine(outputDir, "coverage_grouped.json"),
                Path.Combine(outputDir, "coverage_grouped.png"),
                "Coverage (grouped holdout)");
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
                runLabelShuffleControl,
                config);
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

    private static void GenerateSingleErrorFigure(
        string dataDir,
        string outputDir,
        double? durationOverride,
        string? outPath,
        string title,
        string regime,
        int maxPoints,
        bool useGroupedSplit)
    {
        string configPath = Path.Combine(outputDir, "pipeline_config.json");
        if (!File.Exists(configPath))
        {
            Console.WriteLine("pipeline_config.json not found in output directory; cannot render single-source error figure.");
            Environment.Exit(1);
        }

        var config = JsonSerializer.Deserialize<PipelineConfiguration>(File.ReadAllText(configPath))
                     ?? throw new InvalidOperationException("Failed to deserialize pipeline_config.json");

        var featureBuilder = new FeatureBuilder(config.Epsilon, config.DipolePositions);
        var mlContext = new MLContext(seed: 42);

        string regressorPath = Path.Combine(outputDir, "single_regressor");
        if (!Directory.Exists(regressorPath))
        {
            Console.WriteLine("single_regressor artifacts not found in output directory; cannot render single-source error figure.");
            Environment.Exit(1);
        }

        var regressor = RegressionModelGroup.Load(mlContext, regressorPath);

        bool applyOodPass = regime.Equals("ood-pass", StringComparison.OrdinalIgnoreCase);
        MahalanobisScorer? mahalanobis = null;
        if (applyOodPass)
        {
            if (!TryLoadMahalanobis(Path.Combine(outputDir, "mahalanobis.json"), out mahalanobis))
            {
                Console.WriteLine("Mahalanobis model not found in artifacts; cannot apply ood-pass regime.");
                Environment.Exit(1);
            }
        }

        var singleRows = LoadSingleGroups(dataDir, durationOverride);
        if (singleRows.Count == 0)
        {
            Console.WriteLine("No single-source rows loaded. Check --data-dir.");
            Environment.Exit(1);
        }

        IReadOnlyList<LocalizationRow> evalRows;
        if (useGroupedSplit)
        {
            var grouped = Grouping.GroupSplit(singleRows, 0.2, 42);
            evalRows = grouped.Holdout;
        }
        else
        {
            var rng = new Random(42);
            var shuffled = singleRows.OrderBy(_ => rng.Next()).ToList();
            int holdoutCount = Math.Max(1, (int)Math.Round(shuffled.Count * 0.2));
            evalRows = shuffled.Take(holdoutCount).ToList();
        }

        if (evalRows.Count == 0)
        {
            Console.WriteLine("Holdout split was empty; cannot generate single-source error figure.");
            Environment.Exit(1);
        }

        int beforeGateCount = evalRows.Count;
        var errors = new List<double>(evalRows.Count);

        foreach (var row in evalRows)
        {
            if (row.SingleCoordinates is not { Length: 3 } truth)
            {
                continue;
            }

            var features = featureBuilder.BuildFeatures(row.Channels, row.DurationSeconds).FeatureVector.Select(f => (float)f).ToArray();
            if (applyOodPass && mahalanobis != null)
            {
                double distance = mahalanobis.Score(ExtractOodVector(features).Select(v => (double)v).ToArray());
                if (distance > config.OutOfDistributionThreshold)
                {
                    continue;
                }
            }

            var prediction = regressor.Predict(features);
            errors.Add(Euclidean(prediction.Take(3).ToArray(), truth));
        }

        if (errors.Count == 0)
        {
            Console.WriteLine("No valid evaluation examples after filtering; cannot generate figure.");
            Environment.Exit(1);
        }

        var sortedErrors = errors.OrderBy(e => e).ToList();
        var plotErrors = sortedErrors;
        if (sortedErrors.Count > maxPoints && maxPoints > 0)
        {
            double stride = sortedErrors.Count / (double)maxPoints;
            var subsampled = new List<double>(maxPoints);
            for (int i = 0; i < maxPoints; i++)
            {
                int idx = (int)Math.Floor(i * stride);
                if (idx >= sortedErrors.Count)
                {
                    idx = sortedErrors.Count - 1;
                }

                subsampled.Add(sortedErrors[idx]);
            }

            subsampled[^1] = sortedErrors[^1];
            plotErrors = subsampled;
        }

        string outputPath = outPath ?? Path.Combine(outputDir, "FigureD_single_source_error_cdf.png");
        string subtitle = $"N_eval={errors.Count}, OOD threshold={config.OutOfDistributionThreshold:F4}, regime={regime}";
        SingleSourceErrorFigureWriter.Write(outputPath, plotErrors, title, subtitle);

        double median = Percentile(sortedErrors, 0.5);
        double p90 = Percentile(sortedErrors, 0.9);
        double p95 = Percentile(sortedErrors, 0.95);

        Console.WriteLine($"Single-source error figure written to {outputPath}");
        Console.WriteLine($"N_total_single_rows={singleRows.Count}");
        if (applyOodPass)
        {
            Console.WriteLine($"N_after_OOD_gate={errors.Count} (holdout before gate={beforeGateCount})");
        }
        else
        {
            Console.WriteLine($"N_eval={errors.Count}");
        }

        Console.WriteLine($"Median error (cm)={median:F3}");
        Console.WriteLine($"90th percentile error (cm)={p90:F3}");
        Console.WriteLine($"95th percentile error (cm)={p95:F3}");
    }

    private static void GenerateDualErrorFigure(
        string dataDir,
        string outputDir,
        double? durationOverride,
        string? outPath,
        string title,
        string regime,
        string metric,
        int maxPoints,
        bool useGroupedSplit)
    {
        string configPath = Path.Combine(outputDir, "pipeline_config.json");
        if (!File.Exists(configPath))
        {
            Console.WriteLine("pipeline_config.json not found in output directory; cannot render dual-source error figure.");
            Environment.Exit(1);
        }

        var config = JsonSerializer.Deserialize<PipelineConfiguration>(File.ReadAllText(configPath))
                     ?? throw new InvalidOperationException("Failed to deserialize pipeline_config.json");

        bool useMean = metric.Equals("mean", StringComparison.OrdinalIgnoreCase);
        bool useMax = metric.Equals("max", StringComparison.OrdinalIgnoreCase);
        if (!useMean && !useMax)
        {
            Console.WriteLine($"Unsupported dual error metric '{metric}'. Expected 'mean' or 'max'.");
            Environment.Exit(1);
        }

        string metricLabel = useMean ? "mean" : "max";

        var featureBuilder = new FeatureBuilder(config.Epsilon, config.DipolePositions);
        var mlContext = new MLContext(seed: 42);

        string regressorPath = Path.Combine(outputDir, "dual_regressor");
        if (!Directory.Exists(regressorPath))
        {
            Console.WriteLine("dual_regressor artifacts not found in output directory; cannot render dual-source error figure.");
            Environment.Exit(1);
        }

        var regressor = RegressionModelGroup.Load(mlContext, regressorPath);

        bool applyOodPass = regime.Equals("ood-pass", StringComparison.OrdinalIgnoreCase);
        MahalanobisScorer? mahalanobis = null;
        if (applyOodPass)
        {
            if (!TryLoadMahalanobis(Path.Combine(outputDir, "mahalanobis.json"), out mahalanobis))
            {
                Console.WriteLine("Mahalanobis model not found in artifacts; cannot apply ood-pass regime.");
                Environment.Exit(1);
            }
        }

        var dualRows = LoadDualGroups(dataDir, durationOverride);
        if (dualRows.Count == 0)
        {
            Console.WriteLine("No dual-source rows loaded. Check --data-dir.");
            Environment.Exit(1);
        }

        IReadOnlyList<LocalizationRow> evalRows;
        if (useGroupedSplit)
        {
            var grouped = Grouping.GroupSplit(dualRows, 0.2, 42);
            evalRows = grouped.Holdout;
        }
        else
        {
            var rng = new Random(42);
            var shuffled = dualRows.OrderBy(_ => rng.Next()).ToList();
            int holdoutCount = Math.Max(1, (int)Math.Round(shuffled.Count * 0.2));
            evalRows = shuffled.Take(holdoutCount).ToList();
        }

        if (evalRows.Count == 0)
        {
            Console.WriteLine("Holdout split was empty; cannot generate dual-source error figure.");
            Environment.Exit(1);
        }

        int beforeGateCount = evalRows.Count;
        var errors = new List<double>(evalRows.Count);

        foreach (var row in evalRows)
        {
            if (row.DualCoordinates is not { Length: 6 } truth)
            {
                continue;
            }

            var features = featureBuilder.BuildFeatures(row.Channels, row.DurationSeconds).FeatureVector.Select(f => (float)f).ToArray();
            if (applyOodPass && mahalanobis != null)
            {
                double distance = mahalanobis.Score(ExtractOodVector(features).Select(v => (double)v).ToArray());
                if (distance > config.OutOfDistributionThreshold)
                {
                    continue;
                }
            }

            var prediction = regressor.Predict(features);
            if (prediction.Length < 6)
            {
                continue;
            }

            var predA = prediction.Take(3).ToArray();
            var predB = prediction.Skip(3).Take(3).ToArray();
            var truthA = truth.Take(3).ToArray();
            var truthB = truth.Skip(3).Take(3).ToArray();

            double eA1 = Euclidean(predA, truthA);
            double eB1 = Euclidean(predB, truthB);
            double agg1 = useMean ? (eA1 + eB1) / 2.0 : Math.Max(eA1, eB1);

            double eA2 = Euclidean(predA, truthB);
            double eB2 = Euclidean(predB, truthA);
            double agg2 = useMean ? (eA2 + eB2) / 2.0 : Math.Max(eA2, eB2);

            errors.Add(Math.Min(agg1, agg2));
        }

        if (errors.Count == 0)
        {
            Console.WriteLine("No valid evaluation examples after filtering; cannot generate figure.");
            Environment.Exit(1);
        }

        var sortedErrors = errors.OrderBy(e => e).ToList();
        var plotErrors = sortedErrors;
        if (sortedErrors.Count > maxPoints && maxPoints > 0)
        {
            double stride = sortedErrors.Count / (double)maxPoints;
            var subsampled = new List<double>(maxPoints);
            for (int i = 0; i < maxPoints; i++)
            {
                int idx = (int)Math.Floor(i * stride);
                if (idx >= sortedErrors.Count)
                {
                    idx = sortedErrors.Count - 1;
                }

                subsampled.Add(sortedErrors[idx]);
            }

            subsampled[^1] = sortedErrors[^1];
            plotErrors = subsampled;
        }

        string outputPath = outPath ?? Path.Combine(outputDir, "FigureE_dual_source_error_cdf.png");
        string subtitle = $"N_eval={errors.Count}, OOD threshold={config.OutOfDistributionThreshold:F4}, metric={metricLabel}";
        DualSourceErrorFigureWriter.Write(outputPath, plotErrors, title, subtitle);

        double median = Percentile(sortedErrors, 0.5);
        double p90 = Percentile(sortedErrors, 0.9);
        double p95 = Percentile(sortedErrors, 0.95);

        Console.WriteLine($"Dual-source error figure written to {outputPath}");
        Console.WriteLine($"N_total_dual_rows={dualRows.Count}");
        if (applyOodPass)
        {
            Console.WriteLine($"N_after_OOD_gate={errors.Count} (holdout before gate={beforeGateCount})");
        }
        else
        {
            Console.WriteLine($"N_eval={errors.Count}");
        }

        Console.WriteLine($"Median error (cm)={median:F3}");
        Console.WriteLine($"90th percentile error (cm)={p90:F3}");
        Console.WriteLine($"95th percentile error (cm)={p95:F3}");
    }

    private static void GenerateOodMahalanobisFigure(
        string dataDir,
        string outputDir,
        double? durationOverride,
        string? outPath,
        string oodFigTitle,
        int bins,
        int maxPoints,
        string thresholdMode,
        double thresholdK,
        string regime,
        bool useGroupedSplit)
    {
        var singleRows = LoadSingleGroups(dataDir, durationOverride);
        var dualRows = LoadDualGroups(dataDir, durationOverride);

        var filteredRows = regime.Equals("single", StringComparison.OrdinalIgnoreCase)
            ? singleRows
            : regime.Equals("dual", StringComparison.OrdinalIgnoreCase)
                ? dualRows
                : singleRows.Concat(dualRows).ToList();

        if (filteredRows.Count == 0)
        {
            Console.WriteLine("No rows available for the requested regime; cannot generate OOD figure.");
            Environment.Exit(1);
        }

        var split = useGroupedSplit
            ? Grouping.GroupSplit(filteredRows, 0.2, 42)
            : Grouping.GroupSplit(filteredRows, 0.2, 42);

        var trainRows = split.Train;
        var evalRows = split.Holdout;

        var trainer = new ModelTrainer();
        var mlContext = trainer.MlContext;
        FeatureBuilder featureBuilder = trainer.FeatureBuilder;

        MahalanobisScorer? mahalanobis = null;
        double threshold = double.NaN;
        bool usedArtifacts = false;
        string thresholdSource = "fit";
        RegressionModelGroup? singleRegressor = null;
        RegressionModelGroup? dualRegressor = null;
        PipelineConfiguration? artifactConfig = null;

        if (thresholdMode.Equals("artifact", StringComparison.OrdinalIgnoreCase))
        {
            string configPath = Path.Combine(outputDir, "pipeline_config.json");
            if (!File.Exists(configPath))
            {
                Console.WriteLine("pipeline_config.json not found in output directory; falling back to threshold-mode=fit.");
            }
            else
            {
                artifactConfig = JsonSerializer.Deserialize<PipelineConfiguration>(File.ReadAllText(configPath));
                if (artifactConfig != null)
                {
                    featureBuilder = new FeatureBuilder(artifactConfig.Epsilon, artifactConfig.DipolePositions);
                    threshold = artifactConfig.OutOfDistributionThreshold;
                    singleRegressor = RegressionModelGroup.Load(mlContext, Path.Combine(outputDir, "single_regressor"));
                    dualRegressor = RegressionModelGroup.Load(mlContext, Path.Combine(outputDir, "dual_regressor"));

                    if (!TryLoadMahalanobis(Path.Combine(outputDir, "mahalanobis.json"), out mahalanobis))
                    {
                        Console.WriteLine("Mahalanobis model not found in artifacts; falling back to threshold-mode=fit.");
                        thresholdMode = "fit";
                    }
                    else
                    {
                        usedArtifacts = true;
                        thresholdSource = "artifact";
                    }
                }
            }
        }

        if (!usedArtifacts)
        {
            var oodVectors = trainRows
                .Select(row => ExtractOodVector(featureBuilder.BuildFeatures(row.Channels, row.DurationSeconds).FeatureVector.Select(f => (float)f).ToArray())
                    .Select(v => (double)v)
                    .ToArray())
                .ToList();

            if (oodVectors.Count == 0)
            {
                Console.WriteLine("Warning: no training rows available to fit Mahalanobis; using identity fallback.");
                mahalanobis = new MahalanobisScorer(new double[4], Matrix4x4.Identity);
                threshold = thresholdK;
            }
            else
            {
                mahalanobis = MahalanobisScorer.FromSamples(oodVectors);

                var trainDistancesForThreshold = oodVectors.Select(v => mahalanobis.Score(v)).ToList();
                double mean = trainDistancesForThreshold.Average();
                double std = Math.Sqrt(trainDistancesForThreshold.Average(d => Math.Pow(d - mean, 2)));
                threshold = mean + thresholdK * std;
            }

            var singleTrainRows = trainRows.Where(r => !r.IsDual).ToList();
            var dualTrainRows = trainRows.Where(r => r.IsDual).ToList();

            singleRegressor = singleTrainRows.Count == 0
                ? TrainFallbackRegressor(trainer, featureBuilder, durationOverride, new[] { "x", "y", "z" })
                : trainer.TrainMultiRegressor(
                    singleTrainRows.Select(r => featureBuilder.BuildFeatures(r.Channels, r.DurationSeconds).FeatureVector.Select(f => (float)f).ToArray()).ToList(),
                    singleTrainRows.Select(r => r.SingleCoordinates ?? new double[3]).ToList(),
                    new[] { "x", "y", "z" });

            dualRegressor = dualTrainRows.Count == 0
                ? TrainFallbackRegressor(trainer, featureBuilder, durationOverride, new[] { "x1", "y1", "z1", "x2", "y2", "z2" })
                : trainer.TrainMultiRegressor(
                    dualTrainRows.Select(r => featureBuilder.BuildFeatures(r.Channels, r.DurationSeconds).FeatureVector.Select(f => (float)f).ToArray()).ToList(),
                    dualTrainRows.Select(r => r.DualCoordinates ?? new double[6]).ToList(),
                    new[] { "x1", "y1", "z1", "x2", "y2", "z2" });
        }

        var trainDistances = trainRows
            .Select(row => ScoreMahalanobis(row))
            .ToList();

        var evalDistances = new List<double>();
        var evalPoints = new List<(double Distance, double ErrorCm)>();

        foreach (var row in evalRows)
        {
            double distance = ScoreMahalanobis(row);
            evalDistances.Add(distance);

            double error = ComputeLocalizationError(row, distance);
            if (!double.IsNaN(error))
            {
                evalPoints.Add((distance, error));
            }
        }

        evalPoints = Downsample(evalPoints, maxPoints);

        string outputPath = outPath ?? Path.Combine(outputDir, "FigureB_ood_mahalanobis.png");
        string subtitle = $"N_eval={evalDistances.Count}, threshold={threshold:F3}";

        OodMahalanobisFigureWriter.Write(
            outputPath,
            trainDistances,
            evalDistances,
            evalPoints,
            threshold,
            bins,
            oodFigTitle,
            subtitle);

        Console.WriteLine($"OOD figure written to {outputPath}");
        Console.WriteLine($"Threshold ({thresholdSource}) = {threshold:F4}");
        if (artifactConfig?.SchemaHash is { Length: > 0 })
        {
            Console.WriteLine($"Schema hash: {artifactConfig.SchemaHash}");
        }
        Console.WriteLine($"N_train={trainDistances.Count}, N_eval={evalDistances.Count}, N_scatter_plotted={evalPoints.Count}");

        double ScoreMahalanobis(LocalizationRow row)
        {
            var features = featureBuilder.BuildFeatures(row.Channels, row.DurationSeconds).FeatureVector.Select(f => (float)f).ToArray();
            var vector = ExtractOodVector(features).Select(v => (double)v).ToArray();
            return mahalanobis!.Score(vector);
        }

        double ComputeLocalizationError(LocalizationRow row, double distance)
        {
            if (!row.IsDual && row.SingleCoordinates is { Length: 3 } singleTruth)
            {
                var pred = singleRegressor!.Predict(featureBuilder.BuildFeatures(row.Channels, row.DurationSeconds).FeatureVector.Select(f => (float)f).ToArray());
                return Euclidean(pred.Take(3).ToArray(), singleTruth);
            }

            if (row.IsDual && row.DualCoordinates is { Length: 6 } dualTruth)
            {
                var pred = dualRegressor!.Predict(featureBuilder.BuildFeatures(row.Channels, row.DurationSeconds).FeatureVector.Select(f => (float)f).ToArray());
                var errors = AssignmentAwareErrors(pred, dualTruth);
                return 0.5 * (errors.First + errors.Second);
            }

            return double.NaN;
        }
    }

    private static void GenerateClassifierFigure(
        string dataDir,
        string outputDir,
        double? durationOverride,
        string? outPath,
        string title,
        int maxPoints,
        double thresholdOverride,
        string regime,
        bool useGroupedSplit)
    {
        string configPath = Path.Combine(outputDir, "pipeline_config.json");
        if (!File.Exists(configPath))
        {
            Console.WriteLine("pipeline_config.json not found in output directory; cannot render classifier figure.");
            Environment.Exit(1);
        }

        var config = JsonSerializer.Deserialize<PipelineConfiguration>(File.ReadAllText(configPath))
                     ?? throw new InvalidOperationException("Failed to deserialize pipeline_config.json");
        var featureBuilder = new FeatureBuilder(config.Epsilon, config.DipolePositions);
        var mlContext = new MLContext(seed: 42);

        string classifierPath = Path.Combine(outputDir, "classifier.zip");
        if (!File.Exists(classifierPath))
        {
            Console.WriteLine("classifier.zip not found in output directory; cannot render classifier figure.");
            Environment.Exit(1);
        }

        using var classifierStream = File.OpenRead(classifierPath);
        var classifier = mlContext.Model.Load(classifierStream, out _);

        bool applyOodPass = regime.Equals("ood-pass", StringComparison.OrdinalIgnoreCase);
        MahalanobisScorer? mahalanobis = null;
        if (applyOodPass)
        {
            if (!TryLoadMahalanobis(Path.Combine(outputDir, "mahalanobis.json"), out mahalanobis))
            {
                Console.WriteLine("Mahalanobis model not found in artifacts; cannot apply ood-pass regime.");
                Environment.Exit(1);
            }
        }

        var singleRows = LoadSingleGroups(dataDir, durationOverride);
        var dualRows = LoadDualGroups(dataDir, durationOverride);
        var allRows = singleRows.Concat(dualRows).ToList();
        if (allRows.Count == 0)
        {
            Console.WriteLine("No rows loaded for classifier evaluation. Check --data-dir.");
            Environment.Exit(1);
        }

        IReadOnlyList<LocalizationRow> evalRows;
        if (useGroupedSplit)
        {
            var grouped = Grouping.GroupSplit(allRows, 0.2, 42);
            evalRows = grouped.Holdout;
        }
        else
        {
            var split = StratifiedThreeWaySplit(
                allRows.Select(r => (Row: r, Example: BuildClassificationExample(featureBuilder, r))).ToList(),
                calibrationFraction: 0.0,
                testFraction: 0.2,
                seed: 42);
            evalRows = split.Test.Select(s => s.Row).ToList();
        }

        if (evalRows.Count == 0)
        {
            Console.WriteLine("Holdout split was empty; cannot generate classifier figure.");
            Environment.Exit(1);
        }

        var predictionEngine = mlContext.Model.CreatePredictionEngine<ClassificationExample, ClassificationPrediction>(classifier);

        int beforeGateCount = evalRows.Count;
        var filteredSamples = new List<(double Probability, bool Label)>();
        foreach (var row in evalRows)
        {
            var example = BuildClassificationExample(featureBuilder, row);
            if (applyOodPass && mahalanobis != null)
            {
                double distance = mahalanobis.Score(ExtractOodVector(example.Features).Select(v => (double)v).ToArray());
                if (distance > config.OutOfDistributionThreshold)
                {
                    continue;
                }
            }

            double probability = predictionEngine.Predict(example).Probability;
            filteredSamples.Add((probability, example.Label));
        }

        if (filteredSamples.Count == 0)
        {
            Console.WriteLine("No evaluation samples remained after filtering; cannot build classifier figure.");
            Environment.Exit(1);
        }

        var samples = DownsampleSamples(filteredSamples, maxPoints);
        double operatingThreshold = config.ClassifierThreshold ?? thresholdOverride;

        const int steps = 200;
        var rocPoints = new List<(double Fpr, double Tpr)>(steps + 1);
        var prPoints = new List<(double Recall, double Precision)>(steps + 1);

        for (int i = 0; i <= steps; i++)
        {
            double threshold = i / (double)steps;
            var eval = EvaluateAt(samples, threshold);
            rocPoints.Add((eval.Fpr, eval.Tpr));
            prPoints.Add((eval.Recall, eval.Precision));
        }

        var operating = EvaluateAt(samples, operatingThreshold);

        double auc = ComputeTrapezoidalAuc(rocPoints.Select(p => (p.Fpr, p.Tpr)).ToList());
        double ap = ComputeTrapezoidalAuc(prPoints.Select(p => (p.Recall, p.Precision)).ToList());

        string outputPath = outPath ?? Path.Combine(outputDir, "FigureC_classifier_roc_pr.png");
        string subtitle = applyOodPass
            ? $"N_before={beforeGateCount}, N_after={filteredSamples.Count}, OOD threshold={config.OutOfDistributionThreshold:F3}"
            : $"N={filteredSamples.Count}";

        ClassifierPerformanceFigureWriter.Write(
            outputPath,
            rocPoints,
            prPoints,
            (operating.Fpr, operating.Tpr),
            (operating.Recall, operating.Precision),
            auc,
            ap,
            operatingThreshold,
            title,
            subtitle);

        double f1 = operating.Precision + operating.Recall <= 0
            ? 0
            : 2 * operating.Precision * operating.Recall / (operating.Precision + operating.Recall);

        Console.WriteLine($"Classifier figure written to {outputPath}");
        if (applyOodPass)
        {
            Console.WriteLine($"N_before={beforeGateCount}, N_after={filteredSamples.Count}, N_plotted={samples.Count}");
        }
        else
        {
            Console.WriteLine($"N_total={samples.Count}");
        }

        Console.WriteLine($"Operating threshold: {operatingThreshold:F3}");
        Console.WriteLine($"Confusion matrix: TP={operating.Tp}, FP={operating.Fp}, TN={operating.Tn}, FN={operating.Fn}");
        Console.WriteLine($"Precision={operating.Precision:F3}, Recall={operating.Recall:F3}, F1={f1:F3}");
        Console.WriteLine($"AUC={auc:F3}, AP={ap:F3}");

        static (double Fpr, double Tpr, double Precision, double Recall, int Tp, int Fp, int Tn, int Fn) EvaluateAt(
            IReadOnlyList<(double Probability, bool Label)> samples,
            double threshold)
        {
            int tp = 0, fp = 0, tn = 0, fn = 0;
            foreach (var sample in samples)
            {
                bool predicted = sample.Probability >= threshold;
                if (predicted && sample.Label)
                {
                    tp++;
                }
                else if (predicted && !sample.Label)
                {
                    fp++;
                }
                else if (!predicted && !sample.Label)
                {
                    tn++;
                }
                else if (!predicted && sample.Label)
                {
                    fn++;
                }
            }

            double tpr = tp + fn == 0 ? 0 : (double)tp / (tp + fn);
            double fpr = fp + tn == 0 ? 0 : (double)fp / (fp + tn);
            double precision = tp + fp == 0 ? 1.0 : (double)tp / (tp + fp);
            double recall = tp + fn == 0 ? 0 : (double)tp / (tp + fn);
            return (fpr, tpr, precision, recall, tp, fp, tn, fn);
        }
    }

    private static bool TryLoadMahalanobis(string path, out MahalanobisScorer scorer)
    {
        scorer = new MahalanobisScorer(new double[4], Matrix4x4.Identity);
        if (!File.Exists(path))
        {
            return false;
        }

        var model = JsonSerializer.Deserialize<MahalanobisModel>(File.ReadAllText(path));
        if (model?.InverseCovariance is not { Count: 4 })
        {
            return false;
        }

        var inv = new Matrix4x4(
            (float)model.InverseCovariance[0][0], (float)model.InverseCovariance[0][1], (float)model.InverseCovariance[0][2], (float)model.InverseCovariance[0][3],
            (float)model.InverseCovariance[1][0], (float)model.InverseCovariance[1][1], (float)model.InverseCovariance[1][2], (float)model.InverseCovariance[1][3],
            (float)model.InverseCovariance[2][0], (float)model.InverseCovariance[2][1], (float)model.InverseCovariance[2][2], (float)model.InverseCovariance[2][3],
            (float)model.InverseCovariance[3][0], (float)model.InverseCovariance[3][1], (float)model.InverseCovariance[3][2], (float)model.InverseCovariance[3][3]);

        scorer = new MahalanobisScorer(model.Mean, inv);
        return true;
    }

    private static List<(double Distance, double ErrorCm)> Downsample(IReadOnlyList<(double Distance, double ErrorCm)> points, int maxPoints)
    {
        if (points.Count <= maxPoints)
        {
            return points.ToList();
        }

        var ordered = points.OrderBy(p => p.Distance).ToList();
        int step = (int)Math.Ceiling((double)ordered.Count / maxPoints);
        var downsampled = new List<(double Distance, double ErrorCm)>();
        for (int i = 0; i < ordered.Count; i += step)
        {
            downsampled.Add(ordered[i]);
        }

        return downsampled;
    }

    private static List<(double Probability, bool Label)> DownsampleSamples(IReadOnlyList<(double Probability, bool Label)> samples, int maxPoints)
    {
        if (samples.Count <= maxPoints)
        {
            return samples.ToList();
        }

        var ordered = samples.OrderBy(s => s.Probability).ToList();
        int step = (int)Math.Ceiling((double)ordered.Count / maxPoints);
        var result = new List<(double Probability, bool Label)>();
        for (int i = 0; i < ordered.Count; i += step)
        {
            result.Add(ordered[i]);
        }

        return result;
    }

    private static double ComputeTrapezoidalAuc(IReadOnlyList<(double X, double Y)> points)
    {
        if (points.Count < 2)
        {
            return 0;
        }

        var ordered = points.OrderBy(p => p.X).ToList();
        double auc = 0;
        for (int i = 1; i < ordered.Count; i++)
        {
            double dx = ordered[i].X - ordered[i - 1].X;
            double avgY = 0.5 * (ordered[i].Y + ordered[i - 1].Y);
            auc += dx * avgY;
        }

        return auc;
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
        bool runLabelShuffleControl,
        PipelineConfiguration config)
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

            var baselineRandomCoverage = ComputeCoverage(pipeline, randomHoldoutRows);
            var permutedRandomCoverage = ComputeCoverage(pipeline, randomHoldoutRows, permutation);
            var baselineGroupedCoverage = ComputeCoverage(pipeline, groupedHoldoutRows);
            var permutedGroupedCoverage = ComputeCoverage(pipeline, groupedHoldoutRows, permutation);

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

            var coveragePayload = new
            {
                Seed = negativeControlSeed,
                Thresholds = new
                {
                    ProbabilityThreshold = config.StrictProbability,
                    OutOfDistributionThreshold = config.OutOfDistributionThreshold
                },
                RandomHoldout = new { Baseline = baselineRandomCoverage, Permuted = permutedRandomCoverage },
                GroupedHoldout = new { Baseline = baselineGroupedCoverage, Permuted = permutedGroupedCoverage }
            };

            File.WriteAllText(
                Path.Combine(outputDir, "negative_control_channel_permutation_coverage.json"),
                JsonSerializer.Serialize(coveragePayload, JsonWithNamedFloats));

            SaveCoveragePlot(
                "Negative control: channel permutation coverage (random holdout)",
                Path.Combine(outputDir, "negative_control_channel_permutation_coverage.png"),
                ("Baseline", baselineRandomCoverage),
                ("Permuted", permutedRandomCoverage));

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

    private static void SaveCoverageArtifacts(CoverageSummary coverage, PipelineConfiguration config, string jsonPath, string plotPath, string title)
    {
        var payload = new
        {
            Coverage = coverage,
            Thresholds = new
            {
                ProbabilityThreshold = config.StrictProbability,
                OutOfDistributionThreshold = config.OutOfDistributionThreshold
            }
        };

        File.WriteAllText(jsonPath, JsonSerializer.Serialize(payload, JsonWithNamedFloats));
        SaveCoveragePlot(title, plotPath, ("Coverage", coverage));
    }

    private static void SaveCoveragePlot(string title, string outputPath, params (string Label, CoverageSummary Coverage)[] series)
    {
        var model = new PlotModel { Title = title };

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

        var colors = new[] { OxyColors.SteelBlue, OxyColors.IndianRed, OxyColors.DarkOliveGreen, OxyColors.SlateGray };

        for (int i = 0; i < series.Length; i++)
        {
            var line = new LineSeries
            {
                Title = series[i].Label,
                StrokeThickness = 2,
                MarkerType = MarkerType.Circle,
                MarkerSize = 4,
                Color = colors[i % colors.Length]
            };

            var coverage = series[i].Coverage;
            line.Points.Add(new DataPoint(0, coverage.SingleFraction));
            line.Points.Add(new DataPoint(1, coverage.DualFraction));
            line.Points.Add(new DataPoint(2, coverage.CentroidFraction));
            line.Points.Add(new DataPoint(3, coverage.UnknownFraction));

            model.Series.Add(line);
        }

        using var stream = File.Open(outputPath, FileMode.Create);
        new PngExporter { Width = 900, Height = 600 }.Export(model, stream);
    }

    private static CoverageSummary ComputeCoverage(LocalizationPipeline pipeline, IReadOnlyList<LocalizationRow> rows, IReadOnlyList<int>? permutation = null)
    {
        int single = 0, dual = 0, centroid = 0, unknown = 0;

        foreach (var row in rows)
        {
            var prediction = pipeline.Predict(row, permutation);

            if (prediction.Label.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
            {
                unknown++;
            }
            else if (prediction.Label.Contains("centroid", StringComparison.OrdinalIgnoreCase))
            {
                centroid++;
            }
            else if (prediction.Label.StartsWith("Dual", StringComparison.OrdinalIgnoreCase))
            {
                dual++;
            }
            else
            {
                single++;
            }
        }

        return new CoverageSummary(single, dual, centroid, unknown);
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

    private static double Percentile(IReadOnlyList<double> sortedValues, double percentile)
    {
        if (sortedValues.Count == 0)
        {
            return double.NaN;
        }

        double clamped = Math.Max(0, Math.Min(1, percentile));
        if (clamped <= 0)
        {
            return sortedValues[0];
        }

        if (clamped >= 1)
        {
            return sortedValues[^1];
        }

        double position = clamped * (sortedValues.Count - 1);
        int lower = (int)Math.Floor(position);
        int upper = (int)Math.Ceiling(position);

        if (lower == upper)
        {
            return sortedValues[lower];
        }

        double fraction = position - lower;
        return sortedValues[lower] + fraction * (sortedValues[upper] - sortedValues[lower]);
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

    private static (string DataDir, string OutputDir, double? DurationOverride, bool UseGroupedSplit, bool ValidateOod, double OodFaultFraction, int OodSeed, bool RunNegativeControls, int NegativeControlSeed, bool RunPermutationControl, bool RunLabelShuffleControl, bool EmitFeatureSchemaTex, string? TexOutPath, string TexCaption, string TexLabel, bool EmitLockedSchemaTex, string? LockedTexOutPath, string LockedTexCaption, string LockedTexLabel, bool EmitNormalizationFigure, string? NormFigOutPath, string NormFigRegime, double NormFigRoundCm, double NormFigMinCountRatio, int NormFigMaxExamples, string NormFigTitle, bool EmitDescriptorFigure, string? DescFigOutPath, int DescFigBins, string DescFigRegime, string DescFigTitle, bool EmitOodFigure, string? OodFigOutPath, string OodFigTitle, int OodFigBins, int OodFigMaxPoints, string OodFigThresholdMode, double OodFigThresholdK, string OodFigRegime, bool EmitClassifierFigure, string? ClfFigOutPath, string ClfFigTitle, int ClfFigMaxPoints, double ClfFigThreshold, string ClfFigRegime, bool EmitSingleErrorFigure, string? SingleErrFigOut, string SingleErrFigTitle, string SingleErrFigRegime, int SingleErrMaxPoints, bool EmitDualErrorFigure, string? DualErrFigOut, string DualErrFigTitle, string DualErrFigRegime, string DualErrErrorMetric, int DualErrMaxPoints) ParseArgs(string[] args)
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
        bool emitFeatureSchemaTex = false;
        string? texOutPath = null;
        string texCaption = "Raw feature schema derived from sliding analysis windows.";
        string texLabel = "tab:raw_feature_schema";
        bool emitLockedSchemaTex = false;
        string? lockedTexOutPath = null;
        string lockedTexCaption = "Locked feature schema and ordering used for both training and runtime inference.";
        string lockedTexLabel = "tab:locked_feature_schema";
        bool emitNormalizationFigure = false;
        string? normFigOutPath = null;
        string normFigRegime = "single";
        double normFigRoundCm = 1.0;
        double normFigMinCountRatio = 2.0;
        int normFigMaxExamples = 2;
        string normFigTitle = "Effect of normalization at identical source position";
        bool emitDescriptorFigure = false;
        string? descFigOutPath = null;
        int descFigBins = 30;
        string descFigRegime = "all";
        string descFigTitle = "Distributional descriptors from normalized channel responses";
        bool emitOodFigure = false;
        string? oodFigOutPath = null;
        string oodFigTitle = "Out-of-distribution detection using Mahalanobis distance";
        int oodFigBins = 40;
        int oodFigMaxPoints = 5000;
        string oodFigThresholdMode = "artifact";
        double oodFigThresholdK = 3.0;
        string oodFigRegime = "both";
        bool emitClassifierFigure = false;
        string? clfFigOutPath = null;
        string clfFigTitle = "Single vs dual hypothesis selection";
        int clfFigMaxPoints = 5000;
        double clfFigThreshold = 0.5;
        string clfFigRegime = "all";
        bool emitSingleErrorFigure = false;
        string? singleErrFigOut = null;
        string singleErrFigTitle = "Single-source localization error distribution";
        string singleErrFigRegime = "ood-pass";
        int singleErrMaxPoints = int.MaxValue;
        bool emitDualErrorFigure = false;
        string? dualErrFigOut = null;
        string dualErrFigTitle = "Dual-source localization error distribution (assignment-aware)";
        string dualErrFigRegime = "ood-pass";
        string dualErrErrorMetric = "mean";
        int dualErrMaxPoints = int.MaxValue;

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
            else if (arg.StartsWith("--emit-feature-schema-tex="))
            {
                emitFeatureSchemaTex = bool.Parse(arg.Substring("--emit-feature-schema-tex=".Length));
            }
            else if (arg.StartsWith("--tex-out="))
            {
                texOutPath = arg.Substring("--tex-out=".Length);
            }
            else if (arg.StartsWith("--tex-caption="))
            {
                texCaption = arg.Substring("--tex-caption=".Length);
            }
            else if (arg.StartsWith("--tex-label="))
            {
                texLabel = arg.Substring("--tex-label=".Length);
            }
            else if (arg.StartsWith("--emit-locked-schema-tex="))
            {
                emitLockedSchemaTex = bool.Parse(arg.Substring("--emit-locked-schema-tex=".Length));
            }
            else if (arg.StartsWith("--locked-tex-out="))
            {
                lockedTexOutPath = arg.Substring("--locked-tex-out=".Length);
            }
            else if (arg.StartsWith("--locked-tex-caption="))
            {
                lockedTexCaption = arg.Substring("--locked-tex-caption=".Length);
            }
            else if (arg.StartsWith("--locked-tex-label="))
            {
                lockedTexLabel = arg.Substring("--locked-tex-label=".Length);
            }
            else if (arg.StartsWith("--emit-normalization-figure="))
            {
                emitNormalizationFigure = bool.Parse(arg.Substring("--emit-normalization-figure=".Length));
            }
            else if (arg.StartsWith("--normfig-out="))
            {
                normFigOutPath = arg.Substring("--normfig-out=".Length);
            }
            else if (arg.StartsWith("--normfig-regime="))
            {
                normFigRegime = arg.Substring("--normfig-regime=".Length);
            }
            else if (arg.StartsWith("--normfig-position-round-cm="))
            {
                normFigRoundCm = double.Parse(arg.Substring("--normfig-position-round-cm=".Length), CultureInfo.InvariantCulture);
            }
            else if (arg.StartsWith("--normfig-min-count-ratio="))
            {
                normFigMinCountRatio = double.Parse(arg.Substring("--normfig-min-count-ratio=".Length), CultureInfo.InvariantCulture);
            }
            else if (arg.StartsWith("--normfig-max-examples="))
            {
                normFigMaxExamples = int.Parse(arg.Substring("--normfig-max-examples=".Length), CultureInfo.InvariantCulture);
            }
            else if (arg.StartsWith("--normfig-title="))
            {
                normFigTitle = arg.Substring("--normfig-title=".Length);
            }
            else if (arg.StartsWith("--emit-descriptor-figure="))
            {
                emitDescriptorFigure = bool.Parse(arg.Substring("--emit-descriptor-figure=".Length));
            }
            else if (arg.StartsWith("--descfig-out="))
            {
                descFigOutPath = arg.Substring("--descfig-out=".Length);
            }
            else if (arg.StartsWith("--descfig-bins="))
            {
                descFigBins = int.Parse(arg.Substring("--descfig-bins=".Length), CultureInfo.InvariantCulture);
            }
            else if (arg.StartsWith("--descfig-regime="))
            {
                descFigRegime = arg.Substring("--descfig-regime=".Length);
            }
            else if (arg.StartsWith("--descfig-title="))
            {
                descFigTitle = arg.Substring("--descfig-title=".Length);
            }
            else if (arg.StartsWith("--emit-ood-figure="))
            {
                emitOodFigure = bool.Parse(arg.Substring("--emit-ood-figure=".Length));
            }
            else if (arg.StartsWith("--oodfig-out="))
            {
                oodFigOutPath = arg.Substring("--oodfig-out=".Length);
            }
            else if (arg.StartsWith("--oodfig-title="))
            {
                oodFigTitle = arg.Substring("--oodfig-title=".Length);
            }
            else if (arg.StartsWith("--oodfig-bins="))
            {
                oodFigBins = int.Parse(arg.Substring("--oodfig-bins=".Length), CultureInfo.InvariantCulture);
            }
            else if (arg.StartsWith("--oodfig-max-points="))
            {
                oodFigMaxPoints = int.Parse(arg.Substring("--oodfig-max-points=".Length), CultureInfo.InvariantCulture);
            }
            else if (arg.StartsWith("--oodfig-threshold-mode="))
            {
                oodFigThresholdMode = arg.Substring("--oodfig-threshold-mode=".Length);
            }
            else if (arg.StartsWith("--oodfig-threshold-k="))
            {
                oodFigThresholdK = double.Parse(arg.Substring("--oodfig-threshold-k=".Length), CultureInfo.InvariantCulture);
            }
            else if (arg.StartsWith("--oodfig-regime="))
            {
                oodFigRegime = arg.Substring("--oodfig-regime=".Length);
            }
            else if (arg.StartsWith("--emit-classifier-figure="))
            {
                emitClassifierFigure = bool.Parse(arg.Substring("--emit-classifier-figure=".Length));
            }
            else if (arg.StartsWith("--clf-fig-out="))
            {
                clfFigOutPath = arg.Substring("--clf-fig-out=".Length);
            }
            else if (arg.StartsWith("--clf-fig-title="))
            {
                clfFigTitle = arg.Substring("--clf-fig-title=".Length);
            }
            else if (arg.StartsWith("--clf-fig-max-points="))
            {
                clfFigMaxPoints = int.Parse(arg.Substring("--clf-fig-max-points=".Length), CultureInfo.InvariantCulture);
            }
            else if (arg.StartsWith("--clf-fig-threshold="))
            {
                clfFigThreshold = double.Parse(arg.Substring("--clf-fig-threshold=".Length), CultureInfo.InvariantCulture);
            }
            else if (arg.StartsWith("--clf-fig-regime="))
            {
                clfFigRegime = arg.Substring("--clf-fig-regime=".Length);
            }
            else if (arg.StartsWith("--emit-single-error-figure="))
            {
                emitSingleErrorFigure = bool.Parse(arg.Substring("--emit-single-error-figure=".Length));
            }
            else if (arg.StartsWith("--singleerr-fig-out="))
            {
                singleErrFigOut = arg.Substring("--singleerr-fig-out=".Length);
            }
            else if (arg.StartsWith("--singleerr-fig-title="))
            {
                singleErrFigTitle = arg.Substring("--singleerr-fig-title=".Length);
            }
            else if (arg.StartsWith("--singleerr-fig-regime="))
            {
                singleErrFigRegime = arg.Substring("--singleerr-fig-regime=".Length);
            }
            else if (arg.StartsWith("--singleerr-max-points="))
            {
                singleErrMaxPoints = int.Parse(arg.Substring("--singleerr-max-points=".Length), CultureInfo.InvariantCulture);
            }
            else if (arg.StartsWith("--emit-dual-error-figure="))
            {
                emitDualErrorFigure = bool.Parse(arg.Substring("--emit-dual-error-figure=".Length));
            }
            else if (arg.StartsWith("--dualerr-fig-out="))
            {
                dualErrFigOut = arg.Substring("--dualerr-fig-out=".Length);
            }
            else if (arg.StartsWith("--dualerr-fig-title="))
            {
                dualErrFigTitle = arg.Substring("--dualerr-fig-title=".Length);
            }
            else if (arg.StartsWith("--dualerr-fig-regime="))
            {
                dualErrFigRegime = arg.Substring("--dualerr-fig-regime=".Length);
            }
            else if (arg.StartsWith("--dualerr-error-metric="))
            {
                dualErrErrorMetric = arg.Substring("--dualerr-error-metric=".Length);
            }
            else if (arg.StartsWith("--dualerr-max-points="))
            {
                dualErrMaxPoints = int.Parse(arg.Substring("--dualerr-max-points=".Length), CultureInfo.InvariantCulture);
            }
        }

        runPermutationControl |= runNegativeControls;
        runLabelShuffleControl |= runNegativeControls;

        if (!string.Equals(normFigRegime, "single", StringComparison.OrdinalIgnoreCase) && !string.Equals(normFigRegime, "dual", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("--normfig-regime must be either 'single' or 'dual'");
        }

        if (normFigRoundCm <= 0)
        {
            throw new ArgumentException("--normfig-position-round-cm must be positive");
        }

        if (normFigMaxExamples < 2)
        {
            throw new ArgumentException("--normfig-max-examples must be at least 2");
        }

        if (!string.Equals(descFigRegime, "all", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(descFigRegime, "single", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(descFigRegime, "dual", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("--descfig-regime must be one of 'all', 'single', or 'dual'");
        }

        if (descFigBins <= 0)
        {
            throw new ArgumentException("--descfig-bins must be positive");
        }

        if (!string.Equals(oodFigThresholdMode, "artifact", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(oodFigThresholdMode, "fit", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("--oodfig-threshold-mode must be one of 'artifact' or 'fit'");
        }

        if (oodFigBins <= 0)
        {
            throw new ArgumentException("--oodfig-bins must be positive");
        }

        if (oodFigMaxPoints <= 0)
        {
            throw new ArgumentException("--oodfig-max-points must be positive");
        }

        if (!string.Equals(oodFigRegime, "single", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(oodFigRegime, "dual", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(oodFigRegime, "both", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("--oodfig-regime must be one of 'single', 'dual', or 'both'");
        }

        if (clfFigMaxPoints <= 0)
        {
            throw new ArgumentException("--clf-fig-max-points must be positive");
        }

        if (clfFigThreshold is < 0 or > 1)
        {
            throw new ArgumentException("--clf-fig-threshold must be between 0 and 1");
        }

        if (!string.Equals(clfFigRegime, "all", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(clfFigRegime, "ood-pass", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("--clf-fig-regime must be one of 'all' or 'ood-pass'");
        }

        if (!string.Equals(singleErrFigRegime, "ood-pass", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(singleErrFigRegime, "all", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("--singleerr-fig-regime must be one of 'ood-pass' or 'all'");
        }

        if (singleErrMaxPoints <= 0)
        {
            throw new ArgumentException("--singleerr-max-points must be positive");
        }

        if (!string.Equals(dualErrFigRegime, "ood-pass", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(dualErrFigRegime, "all", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("--dualerr-fig-regime must be one of 'ood-pass' or 'all'");
        }

        if (!string.Equals(dualErrErrorMetric, "mean", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(dualErrErrorMetric, "max", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("--dualerr-error-metric must be one of 'mean' or 'max'");
        }

        if (dualErrMaxPoints <= 0)
        {
            throw new ArgumentException("--dualerr-max-points must be positive");
        }

        return (dataDir, outputDir, duration, useGroupedSplit, validateOod, oodFaultFraction, oodSeed, runNegativeControls, negativeControlSeed, runPermutationControl, runLabelShuffleControl, emitFeatureSchemaTex, texOutPath, texCaption, texLabel, emitLockedSchemaTex, lockedTexOutPath, lockedTexCaption, lockedTexLabel, emitNormalizationFigure, normFigOutPath, normFigRegime, normFigRoundCm, normFigMinCountRatio, normFigMaxExamples, normFigTitle, emitDescriptorFigure, descFigOutPath, descFigBins, descFigRegime, descFigTitle, emitOodFigure, oodFigOutPath, oodFigTitle, oodFigBins, oodFigMaxPoints, oodFigThresholdMode, oodFigThresholdK, oodFigRegime, emitClassifierFigure, clfFigOutPath, clfFigTitle, clfFigMaxPoints, clfFigThreshold, clfFigRegime, emitSingleErrorFigure, singleErrFigOut, singleErrFigTitle, singleErrFigRegime, singleErrMaxPoints, emitDualErrorFigure, dualErrFigOut, dualErrFigTitle, dualErrFigRegime, dualErrErrorMetric, dualErrMaxPoints);
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

    private static NormalizationSelection? FindNormalizationExamples(IReadOnlyList<LocalizationRow> singleRows, IReadOnlyList<LocalizationRow> dualRows, string normFigRegime, double normFigRoundCm, double normFigMinCountRatio, int normFigMaxExamples)
    {
        if (normFigRoundCm <= 0)
        {
            throw new ArgumentException("Rounding increment must be positive", nameof(normFigRoundCm));
        }

        bool isDualRegime = string.Equals(normFigRegime, "dual", StringComparison.OrdinalIgnoreCase);

        var candidates = isDualRegime
            ? dualRows.Where(r => r.IsDual && r.DualCoordinates?.Length == 6).ToList()
            : singleRows.Where(r => !r.IsDual && r.SingleCoordinates?.Length == 3).ToList();

        var indexed = candidates.Select((row, index) =>
        {
            var position = isDualRegime
                ? GetCentroid(row.DualCoordinates!)
                : GetSinglePosition(row.SingleCoordinates!);
            var rounded = RoundPosition(position, normFigRoundCm);
            double total = row.Channels.Sum();
            return new { Row = row, Index = index, Position = position, Rounded = rounded, Total = total };
        }).ToList();

        var groups = indexed
            .GroupBy(i => i.Rounded)
            .OrderBy(g => g.Key.Item1)
            .ThenBy(g => g.Key.Item2)
            .ThenBy(g => g.Key.Item3);

        foreach (var group in groups)
        {
            if (group.Count() < 2)
            {
                continue;
            }

            var ordered = group.OrderBy(g => g.Total).ThenBy(g => g.Index).ToList();
            var low = ordered.First();
            var high = ordered.Last();

            if (low.Total <= 0)
            {
                continue;
            }

            double ratio = high.Total / low.Total;
            if (ratio < normFigMinCountRatio)
            {
                continue;
            }

            return new NormalizationSelection(low.Row, high.Row, low.Position, group.Key, low.Total, high.Total);
        }

        return null;
    }

    private static (double X, double Y, double Z) GetSinglePosition(IReadOnlyList<double> coords)
    {
        return (coords[0], coords[1], coords[2]);
    }

    private static (double X, double Y, double Z) GetCentroid(IReadOnlyList<double> coords)
    {
        double x = 0.5 * (coords[0] + coords[3]);
        double y = 0.5 * (coords[1] + coords[4]);
        double z = 0.5 * (coords[2] + coords[5]);
        return (x, y, z);
    }

    private static (int X, int Y, int Z) RoundPosition((double X, double Y, double Z) position, double roundCm)
    {
        int x = (int)Math.Round(position.X / roundCm, MidpointRounding.AwayFromZero);
        int y = (int)Math.Round(position.Y / roundCm, MidpointRounding.AwayFromZero);
        int z = (int)Math.Round(position.Z / roundCm, MidpointRounding.AwayFromZero);
        return (x, y, z);
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
