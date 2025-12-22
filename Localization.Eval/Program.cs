using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Localization.ML;
using Microsoft.ML;
using OxyPlot;
using OxyPlot.Axes;
using OxyPlot.Annotations;
using OxyPlot.Series;
using OxyPlot.SkiaSharp;
using SkiaSharp;

namespace Localization.Eval;

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

    public static int Main(string[] args)
    {
        var (dataDir, artifactsDir, outputDir, durationOverride) = ParseArgs(args);
        Directory.CreateDirectory(outputDir);

        var configPath = Path.Combine(artifactsDir, "pipeline_config.json");
        if (!File.Exists(configPath))
        {
            Console.Error.WriteLine($"Missing pipeline configuration: {configPath}");
            return 1;
        }

        var config = JsonSerializer.Deserialize<PipelineConfiguration>(File.ReadAllText(configPath))
                     ?? throw new InvalidOperationException("Missing pipeline configuration");
        var featureBuilder = new FeatureBuilder(config.Epsilon, config.DipolePositions);
        var mlContext = new MLContext(seed: 42);

        using var classifierStream = File.OpenRead(Path.Combine(artifactsDir, "classifier.zip"));
        var classifier = mlContext.Model.Load(classifierStream, out _);
        var singleRegressor = RegressionModelGroup.Load(mlContext, Path.Combine(artifactsDir, "single_regressor"));
        var dualRegressor = RegressionModelGroup.Load(mlContext, Path.Combine(artifactsDir, "dual_regressor"));
        var mahalanobis = LoadMahalanobis(artifactsDir);

        var singleRows = LoadSingleGroups(dataDir, durationOverride);
        var dualRows = LoadDualGroups(dataDir, durationOverride);
        var allRows = singleRows.Concat(dualRows).ToList();

        Console.WriteLine($"Loaded {singleRows.Count} single-source rows and {dualRows.Count} dual-source rows");

        BuildClassifierPlots(featureBuilder, mlContext, classifier, allRows, config, outputDir);
        BuildSingleSourceCdf(featureBuilder, singleRegressor, singleRows, outputDir);
        BuildDualSourceCdf(featureBuilder, mlContext, classifier, dualRegressor, mahalanobis, config, dualRows, outputDir);

        Console.WriteLine($"Figures saved to {Path.GetFullPath(outputDir)}");
        return 0;
    }

    private static void BuildClassifierPlots(
        FeatureBuilder featureBuilder,
        MLContext mlContext,
        ITransformer classifier,
        IReadOnlyList<LocalizationRow> rows,
        PipelineConfiguration config,
        string outputDir)
    {
        var examples = rows.Select(row =>
        {
            var features = featureBuilder.BuildFeatures(row.Channels, row.DurationSeconds).FeatureVector;
            featureBuilder.EnsureFeatureParity(features);
            return new ClassificationExample
            {
                Label = row.IsDual,
                Features = features.Select(f => (float)f).ToArray()
            };
        }).ToList();

        if (examples.Count == 0)
        {
            Console.WriteLine("No classifier examples available; skipping classifier plot.");
            return;
        }

        var (_, test) = StratifiedSplit(examples, 0.25, seed: 42);
        var testList = test.ToList();

        var engine = mlContext.Model.CreatePredictionEngine<ClassificationExample, ClassificationPrediction>(classifier);
        var labels = new List<bool>(testList.Count);
        var probabilities = new List<double>(testList.Count);

        foreach (var example in testList)
        {
            var prediction = engine.Predict(example);
            labels.Add(example.Label);
            probabilities.Add(prediction.Probability);
        }

        double[] thresholds = Enumerable.Range(0, 501).Select(i => i / 500.0).ToArray();
        var rocPoints = thresholds.Select(t => ComputeRocPoint(labels, probabilities, t)).ToList();
        var prPoints = thresholds.Select(t => ComputePrPoint(labels, probabilities, t)).ToList();

        const double operatingThreshold = 0.5;
        var operatingRoc = ComputeRocPoint(labels, probabilities, operatingThreshold);
        var operatingPr = ComputePrPoint(labels, probabilities, operatingThreshold);

        Console.WriteLine("Classifier holdout evaluation:");
        Console.WriteLine($"  Test rows: {testList.Count}");
        Console.WriteLine($"  Operating threshold: {operatingThreshold:F2}");
        Console.WriteLine($"  ROC @ {operatingThreshold:F2}: TPR={operatingRoc.TruePositiveRate:F3}, FPR={operatingRoc.FalsePositiveRate:F3}");
        Console.WriteLine($"  PR  @ {operatingThreshold:F2}: Precision={operatingPr.Precision:F3}, Recall={operatingPr.Recall:F3}");
        Console.WriteLine($"  Strict override threshold: {config.StrictProbability:F2}");

        var rocModel = BuildRocModel(rocPoints, operatingRoc);
        var prModel = BuildPrModel(prPoints, operatingPr);

        var outputPath = Path.Combine(outputDir, "classifier_roc_pr.png");
        SaveSideBySide(rocModel, prModel, outputPath, 900, 600);
    }

    private static void BuildSingleSourceCdf(
        FeatureBuilder featureBuilder,
        RegressionModelGroup regressor,
        IReadOnlyList<LocalizationRow> rows,
        string outputDir)
    {
        if (rows.Count == 0)
        {
            Console.WriteLine("No single-source rows available; skipping single-source CDF plot.");
            return;
        }

        var (_, test) = SplitHoldout(rows, seed: 99);
        var testList = test.ToList();
        var errors = new List<double>(testList.Count);

        foreach (var row in testList)
        {
            var features = featureBuilder.BuildFeatures(row.Channels, row.DurationSeconds).FeatureVector;
            featureBuilder.EnsureFeatureParity(features);
            var prediction = regressor.Predict(features.Select(f => (float)f).ToArray());
            var truth = row.SingleCoordinates ?? Array.Empty<double>();
            errors.Add(Euclidean(prediction, truth));
        }

        var sorted = errors.OrderBy(v => v).ToArray();
        var median = Percentile(sorted, 0.5);
        var p90 = Percentile(sorted, 0.9);

        Console.WriteLine("Single-source localization error:");
        Console.WriteLine($"  Test rows: {testList.Count}");
        Console.WriteLine($"  Median error (cm): {median:F2}");
        Console.WriteLine($"  90th percentile error (cm): {p90:F2}");

        var (xs, ys) = BuildCdf(sorted);
        var model = BuildCdfModel(
            xs,
            ys,
            "Single-source Localization Error CDF",
            "Error (cm)",
            median,
            p90,
            new[] { "Median", "90th percentile" });

        var outputPath = Path.Combine(outputDir, "single_source_error_cdf.png");
        var exporter = new PngExporter { Width = 900, Height = 600 };
        using (var stream = File.Open(outputPath, FileMode.Create))
        {
            exporter.Export(model, stream);
        }
    }

    private static void BuildDualSourceCdf(
        FeatureBuilder featureBuilder,
        MLContext mlContext,
        ITransformer classifier,
        RegressionModelGroup regressor,
        MahalanobisScorer mahalanobis,
        PipelineConfiguration config,
        IReadOnlyList<LocalizationRow> rows,
        string outputDir)
    {
        if (rows.Count == 0)
        {
            Console.WriteLine("No dual-source rows available; skipping dual-source CDF plot.");
            return;
        }

        var (_, test) = SplitHoldout(rows, seed: 99);
        var testList = test.ToList();

        var engine = mlContext.Model.CreatePredictionEngine<ClassificationExample, ClassificationPrediction>(classifier);
        var idErrors = new List<double>();
        var oodErrors = new List<double>();
        int centroidOverrideCount = 0;

        foreach (var row in testList)
        {
            var features = featureBuilder.BuildFeatures(row.Channels, row.DurationSeconds).FeatureVector;
            featureBuilder.EnsureFeatureParity(features);
            var prediction = regressor.Predict(features.Select(f => (float)f).ToArray());
            var truth = row.DualCoordinates ?? Array.Empty<double>();

            var classPrediction = engine.Predict(new ClassificationExample
            {
                Features = features.Select(f => (float)f).ToArray(),
                Label = row.IsDual
            });

            if (IsCentroidOverride(prediction, classPrediction.Probability, config))
            {
                centroidOverrideCount++;
                continue;
            }

            var errors = AssignmentAwareErrors(prediction, truth);
            var oodScore = mahalanobis.Score(ExtractOodFeatures(features));
            bool isOod = oodScore > config.OutOfDistributionThreshold;
            if (isOod)
            {
                oodErrors.Add(errors.First);
                oodErrors.Add(errors.Second);
            }
            else
            {
                idErrors.Add(errors.First);
                idErrors.Add(errors.Second);
            }
        }

        Console.WriteLine("Dual-source localization error:");
        Console.WriteLine($"  Test rows: {testList.Count}");
        Console.WriteLine($"  Centroid override exclusions: {centroidOverrideCount}");
        Console.WriteLine($"  In-distribution samples: {idErrors.Count}");
        Console.WriteLine($"  OOD samples: {oodErrors.Count}");

        if (idErrors.Count > 0)
        {
            var sortedId = idErrors.OrderBy(v => v).ToArray();
            Console.WriteLine($"  ID median error (cm): {Percentile(sortedId, 0.5):F2}");
            Console.WriteLine($"  ID 90th percentile error (cm): {Percentile(sortedId, 0.9):F2}");
        }

        if (oodErrors.Count > 0)
        {
            var sortedOod = oodErrors.OrderBy(v => v).ToArray();
            Console.WriteLine($"  OOD median error (cm): {Percentile(sortedOod, 0.5):F2}");
            Console.WriteLine($"  OOD 90th percentile error (cm): {Percentile(sortedOod, 0.9):F2}");
        }

        var model = BuildDualCdfModel(idErrors, oodErrors);
        var outputPath = Path.Combine(outputDir, "dual_source_error_cdf_ood.png");
        var exporter = new PngExporter { Width = 900, Height = 600 };
        using (var stream = File.Open(outputPath, FileMode.Create))
        {
            exporter.Export(model, stream);
        }
    }

    private static PlotModel BuildRocModel(IReadOnlyList<RocPoint> points, RocPoint operatingPoint)
    {
        var model = CreateNormalizedModel("Classifier ROC Curve", "False Positive Rate", "True Positive Rate");
        var rocSeries = new LineSeries { Title = "ROC", StrokeThickness = 2, Color = OxyColors.SteelBlue };
        foreach (var point in points)
        {
            rocSeries.Points.Add(new DataPoint(point.FalsePositiveRate, point.TruePositiveRate));
        }

        var diagonal = new LineSeries { Title = "Chance", StrokeThickness = 1, Color = OxyColors.Gray, LineStyle = LineStyle.Dash };
        diagonal.Points.Add(new DataPoint(0, 0));
        diagonal.Points.Add(new DataPoint(1, 1));

        var opSeries = new ScatterSeries { Title = "Operating", MarkerType = MarkerType.Circle, MarkerSize = 4, MarkerFill = OxyColors.DarkRed };
        opSeries.Points.Add(new ScatterPoint(operatingPoint.FalsePositiveRate, operatingPoint.TruePositiveRate));

        model.Series.Add(rocSeries);
        model.Series.Add(diagonal);
        model.Series.Add(opSeries);
        model.IsLegendVisible = true;
        return model;
    }

    private static PlotModel BuildPrModel(IReadOnlyList<PrPoint> points, PrPoint operatingPoint)
    {
        var model = CreateNormalizedModel("Classifier Precision-Recall", "Recall", "Precision");
        var series = new LineSeries { Title = "PR", StrokeThickness = 2, Color = OxyColors.MediumSeaGreen };
        foreach (var point in points)
        {
            series.Points.Add(new DataPoint(point.Recall, point.Precision));
        }

        var opSeries = new ScatterSeries { Title = "Operating", MarkerType = MarkerType.Circle, MarkerSize = 4, MarkerFill = OxyColors.DarkRed };
        opSeries.Points.Add(new ScatterPoint(operatingPoint.Recall, operatingPoint.Precision));

        model.Series.Add(series);
        model.Series.Add(opSeries);
        model.IsLegendVisible = true;
        return model;
    }

    private static PlotModel BuildCdfModel(
        double[] xs,
        double[] ys,
        string title,
        string xLabel,
        double median,
        double p90,
        string[] labelText)
    {
        var model = CreateCdfModel(title, xLabel, "CDF");
        var series = new LineSeries { Title = "CDF", StrokeThickness = 2, Color = OxyColors.SteelBlue };
        for (int i = 0; i < xs.Length; i++)
        {
            series.Points.Add(new DataPoint(xs[i], ys[i]));
        }

        var medianLine = new LineAnnotation
        {
            Type = LineAnnotationType.Vertical,
            X = median,
            Color = OxyColors.DarkGreen,
            LineStyle = LineStyle.Dash,
            Text = $"{labelText[0]}: {median:F2} cm"
        };

        var p90Line = new LineAnnotation
        {
            Type = LineAnnotationType.Vertical,
            X = p90,
            Color = OxyColors.DarkOrange,
            LineStyle = LineStyle.Dash,
            Text = $"{labelText[1]}: {p90:F2} cm"
        };

        model.Series.Add(series);
        model.Annotations.Add(medianLine);
        model.Annotations.Add(p90Line);
        model.IsLegendVisible = false;
        return model;
    }

    private static PlotModel BuildDualCdfModel(IReadOnlyList<double> idErrors, IReadOnlyList<double> oodErrors)
    {
        var model = CreateCdfModel("Dual-source Localization Error CDF (ID vs OOD)", "Error (cm)", "CDF");
        if (idErrors.Count > 0)
        {
            var sorted = idErrors.OrderBy(v => v).ToArray();
            var (xs, ys) = BuildCdf(sorted);
            var series = new LineSeries { Title = "In-distribution", StrokeThickness = 2, Color = OxyColors.SteelBlue };
            for (int i = 0; i < xs.Length; i++)
            {
                series.Points.Add(new DataPoint(xs[i], ys[i]));
            }
            model.Series.Add(series);
        }

        if (oodErrors.Count > 0)
        {
            var sorted = oodErrors.OrderBy(v => v).ToArray();
            var (xs, ys) = BuildCdf(sorted);
            var series = new LineSeries { Title = "OOD", StrokeThickness = 2, Color = OxyColors.IndianRed, LineStyle = LineStyle.Dash };
            for (int i = 0; i < xs.Length; i++)
            {
                series.Points.Add(new DataPoint(xs[i], ys[i]));
            }
            model.Series.Add(series);
        }

        model.IsLegendVisible = true;
        return model;
    }

    private static PlotModel CreateNormalizedModel(string title, string xLabel, string yLabel)
    {
        var model = new PlotModel
        {
            Title = title,
            DefaultFontSize = 14,
            TitleFontSize = 18
        };

        model.Axes.Add(new LinearAxis
        {
            Position = AxisPosition.Bottom,
            Title = xLabel,
            Minimum = 0,
            Maximum = 1,
            MajorGridlineStyle = LineStyle.Solid,
            MinorGridlineStyle = LineStyle.Dot,
            FontSize = 14
        });

        model.Axes.Add(new LinearAxis
        {
            Position = AxisPosition.Left,
            Title = yLabel,
            Minimum = 0,
            Maximum = 1,
            MajorGridlineStyle = LineStyle.Solid,
            MinorGridlineStyle = LineStyle.Dot,
            FontSize = 14
        });

        return model;
    }

    private static PlotModel CreateCdfModel(string title, string xLabel, string yLabel)
    {
        var model = new PlotModel
        {
            Title = title,
            DefaultFontSize = 14,
            TitleFontSize = 18
        };

        model.Axes.Add(new LinearAxis
        {
            Position = AxisPosition.Bottom,
            Title = xLabel,
            Minimum = 0,
            MajorGridlineStyle = LineStyle.Solid,
            MinorGridlineStyle = LineStyle.Dot,
            FontSize = 14
        });

        model.Axes.Add(new LinearAxis
        {
            Position = AxisPosition.Left,
            Title = yLabel,
            Minimum = 0,
            Maximum = 1,
            MajorGridlineStyle = LineStyle.Solid,
            MinorGridlineStyle = LineStyle.Dot,
            FontSize = 14
        });

        return model;
    }

    private static void SaveSideBySide(PlotModel left, PlotModel right, string outputPath, int width, int height)
    {
        using var leftBitmap = RenderToBitmap(left, width, height);
        using var rightBitmap = RenderToBitmap(right, width, height);

        int combinedWidth = leftBitmap.Width + rightBitmap.Width;
        int combinedHeight = Math.Max(leftBitmap.Height, rightBitmap.Height);

        using var combined = new SKBitmap(combinedWidth, combinedHeight);
        using (var canvas = new SKCanvas(combined))
        {
            canvas.Clear(SKColors.White);
            canvas.DrawBitmap(leftBitmap, 0, 0);
            canvas.DrawBitmap(rightBitmap, leftBitmap.Width, 0);
        }

        using var image = SKImage.FromBitmap(combined);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        using var stream = File.Open(outputPath, FileMode.Create);
        data.SaveTo(stream);
    }

    private static SKBitmap RenderToBitmap(PlotModel model, int width, int height)
    {
        var exporter = new PngExporter { Width = width, Height = height };
        using var stream = new MemoryStream();
        exporter.Export(model, stream);
        stream.Position = 0;
        return SKBitmap.Decode(stream);
    }

    private static (IReadOnlyList<ClassificationExample> Train, IReadOnlyList<ClassificationExample> Test) StratifiedSplit(
        IReadOnlyList<ClassificationExample> data,
        double testFraction,
        int seed)
    {
        var grouped = data.GroupBy(d => d.Label).ToDictionary(g => g.Key, g => g.ToList());
        var train = new List<ClassificationExample>();
        var test = new List<ClassificationExample>();
        var rnd = new Random(seed);

        foreach (var kvp in grouped)
        {
            int testCount = (int)Math.Round(kvp.Value.Count * testFraction);
            var shuffled = kvp.Value.OrderBy(_ => rnd.Next()).ToList();
            test.AddRange(shuffled.Take(testCount));
            train.AddRange(shuffled.Skip(testCount));
        }

        return (train, test);
    }

    private static (IReadOnlyList<T> Train, IReadOnlyList<T> Test) SplitHoldout<T>(IReadOnlyList<T> data, int seed)
    {
        var rnd = new Random(seed);
        var indices = Enumerable.Range(0, data.Count).OrderBy(_ => rnd.Next()).ToList();
        int testCount = Math.Max(1, data.Count / 4);
        var testIdx = indices.Take(testCount).ToHashSet();

        var train = new List<T>();
        var test = new List<T>();

        for (int i = 0; i < data.Count; i++)
        {
            if (testIdx.Contains(i))
            {
                test.Add(data[i]);
            }
            else
            {
                train.Add(data[i]);
            }
        }

        return (train, test);
    }

    private static RocPoint ComputeRocPoint(IReadOnlyList<bool> labels, IReadOnlyList<double> probabilities, double threshold)
    {
        int tp = 0;
        int fp = 0;
        int tn = 0;
        int fn = 0;

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
        int tp = 0;
        int fp = 0;
        int fn = 0;

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

    private static (double[] Xs, double[] Ys) BuildCdf(double[] sorted)
    {
        if (sorted.Length == 0)
        {
            return (Array.Empty<double>(), Array.Empty<double>());
        }

        double[] ys = new double[sorted.Length];
        for (int i = 0; i < sorted.Length; i++)
        {
            ys[i] = (i + 1) / (double)sorted.Length;
        }

        return (sorted, ys);
    }

    private static double Percentile(double[] sorted, double percentile)
    {
        if (sorted.Length == 0)
        {
            return double.NaN;
        }

        double position = (sorted.Length - 1) * percentile;
        int lower = (int)Math.Floor(position);
        int upper = (int)Math.Ceiling(position);
        if (lower == upper)
        {
            return sorted[lower];
        }

        double weight = position - lower;
        return sorted[lower] * (1 - weight) + sorted[upper] * weight;
    }

    private static double Euclidean(double[] predicted, double[] actual)
    {
        if (predicted.Length != actual.Length)
        {
            throw new InvalidOperationException("Prediction/target length mismatch.");
        }

        double sum = 0;
        for (int i = 0; i < predicted.Length; i++)
        {
            sum += Math.Pow(predicted[i] - actual[i], 2);
        }

        return Math.Sqrt(sum);
    }

    private static (double First, double Second) AssignmentAwareErrors(double[] prediction, double[] truth)
    {
        if (prediction.Length < 6 || truth.Length < 6)
        {
            return (double.NaN, double.NaN);
        }

        var p1 = prediction.Take(3).ToArray();
        var p2 = prediction.Skip(3).Take(3).ToArray();
        var t1 = truth.Take(3).ToArray();
        var t2 = truth.Skip(3).Take(3).ToArray();

        double d11 = Euclidean(p1, t1);
        double d22 = Euclidean(p2, t2);
        double d12 = Euclidean(p1, t2);
        double d21 = Euclidean(p2, t1);

        if (d11 + d22 <= d12 + d21)
        {
            return (d11, d22);
        }

        return (d12, d21);
    }

    private static bool IsCentroidOverride(double[] prediction, double probability, PipelineConfiguration config)
    {
        if (prediction.Length < 6)
        {
            return false;
        }

        var first = prediction.Take(3).ToArray();
        var second = prediction.Skip(3).Take(3).ToArray();
        double separation = Euclidean(first, second);
        return separation < config.MinimumSeparationCm && probability < config.StrictProbability;
    }

    private static double[] ExtractOodFeatures(IReadOnlyList<double> features)
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

    private static MahalanobisScorer LoadMahalanobis(string artifactsDir)
    {
        var path = Path.Combine(artifactsDir, "mahalanobis.json");
        if (!File.Exists(path))
        {
            return new MahalanobisScorer(new double[4] { 0, 0, 0, 0 }, Matrix4x4.Identity);
        }

        var model = JsonSerializer.Deserialize<MahalanobisModel>(File.ReadAllText(path));
        if (model == null)
        {
            return new MahalanobisScorer(new double[4] { 0, 0, 0, 0 }, Matrix4x4.Identity);
        }

        var matrix = new Matrix4x4(
            (float)model.InverseCovariance[0][0], (float)model.InverseCovariance[0][1], (float)model.InverseCovariance[0][2], (float)model.InverseCovariance[0][3],
            (float)model.InverseCovariance[1][0], (float)model.InverseCovariance[1][1], (float)model.InverseCovariance[1][2], (float)model.InverseCovariance[1][3],
            (float)model.InverseCovariance[2][0], (float)model.InverseCovariance[2][1], (float)model.InverseCovariance[2][2], (float)model.InverseCovariance[2][3],
            (float)model.InverseCovariance[3][0], (float)model.InverseCovariance[3][1], (float)model.InverseCovariance[3][2], (float)model.InverseCovariance[3][3]);

        return new MahalanobisScorer(model.Mean, matrix);
    }

    private static (string DataDir, string ArtifactsDir, string OutputDir, double? DurationOverride) ParseArgs(string[] args)
    {
        string dataDir = ".";
        string artifactsDir = "artifacts";
        string outputDir = "figures";
        double? duration = null;

        foreach (var arg in args)
        {
            if (arg.StartsWith("--data-dir=", StringComparison.OrdinalIgnoreCase))
            {
                dataDir = arg.Substring("--data-dir=".Length);
            }
            else if (arg.StartsWith("--artifacts-dir=", StringComparison.OrdinalIgnoreCase))
            {
                artifactsDir = arg.Substring("--artifacts-dir=".Length);
            }
            else if (arg.StartsWith("--output-dir=", StringComparison.OrdinalIgnoreCase))
            {
                outputDir = arg.Substring("--output-dir=".Length);
            }
            else if (arg.StartsWith("--duration=", StringComparison.OrdinalIgnoreCase))
            {
                duration = double.Parse(arg.Substring("--duration=".Length), CultureInfo.InvariantCulture);
            }
        }

        return (dataDir, artifactsDir, outputDir, duration);
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
                if (!string.Equals(extension, ".csv", StringComparison.OrdinalIgnoreCase))
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

    private sealed record RocPoint(double FalsePositiveRate, double TruePositiveRate);
    private sealed record PrPoint(double Recall, double Precision);
}

internal static class DatasetLoader
{
    private static readonly string[] PreferredJoinKeys = { "pair_id", "pairid", "pair", "id", "index", "row" };

    public static IReadOnlyList<LocalizationRow> LoadSingleSource(string path, double? fallbackDuration)
    {
        return LoadSingleInternal(path, fallbackDuration);
    }

    public static IReadOnlyList<LocalizationRow> LoadDualSource(string path, double? fallbackDuration)
    {
        return LoadDualSource(path, pairMetadataPath: null, fallbackDuration);
    }

    public static IReadOnlyList<LocalizationRow> LoadDualSource(string path, string? pairMetadataPath, double? fallbackDuration)
    {
        var countsTable = LoadTable(path);
        if (countsTable.Rows.Count == 0)
        {
            return Array.Empty<LocalizationRow>();
        }

        var channelIndexes = ResolveChannelIndexes(countsTable, path);
        int? durationIndex = TryGetIndex(countsTable.HeaderMap, "duration_s");
        double? inferredDuration = fallbackDuration ?? InferDurationFromName(path);

        var coordinateTable = pairMetadataPath == null
            ? countsTable
            : LoadTable(pairMetadataPath);

        var coordIndexes = ResolveDualCoordinateIndexes(coordinateTable, pairMetadataPath ?? path);

        string? joinKey = FindJoinKey(countsTable.Headers, coordinateTable.Headers);
        var rows = new List<LocalizationRow>();
        if (joinKey != null)
        {
            var metadataLookup = BuildMetadataLookup(coordinateTable, coordIndexes, joinKey);
            foreach (var countRow in countsTable.Rows)
            {
                if (!TryGetValue(countsTable, countRow, joinKey, out var keyValue))
                {
                    continue;
                }

                if (!metadataLookup.TryGetValue(keyValue, out var dualCoords))
                {
                    continue;
                }

                rows.Add(CreateDualRow(countRow, channelIndexes, durationIndex, inferredDuration, dualCoords));
            }
        }
        else
        {
            int rowCount = Math.Min(countsTable.Rows.Count, coordinateTable.Rows.Count);
            for (int i = 0; i < rowCount; i++)
            {
                var dualCoords = ParseDualCoords(coordinateTable.Rows[i], coordIndexes);
                rows.Add(CreateDualRow(countsTable.Rows[i], channelIndexes, durationIndex, inferredDuration, dualCoords));
            }
        }

        return rows;
    }

    private static IReadOnlyList<LocalizationRow> LoadSingleInternal(string path, double? fallbackDuration)
    {
        var table = LoadTable(path);
        if (table.Rows.Count == 0)
        {
            return Array.Empty<LocalizationRow>();
        }

        var channelIndexes = ResolveChannelIndexes(table, path);
        int? durationIndex = TryGetIndex(table.HeaderMap, "duration_s");
        double? inferredDuration = fallbackDuration ?? InferDurationFromName(path);

        int? xIndex = TryGetIndex(table.HeaderMap, "x");
        int? yIndex = TryGetIndex(table.HeaderMap, "y");
        int? zIndex = TryGetIndex(table.HeaderMap, "z");
        int? fileNameIndex = TryGetIndex(table.HeaderMap, "File Name")
            ?? TryGetIndex(table.HeaderMap, "FileName")
            ?? TryGetIndex(table.HeaderMap, "filename");

        bool hasExplicitCoords = xIndex.HasValue && yIndex.HasValue && zIndex.HasValue;
        if (!hasExplicitCoords && !fileNameIndex.HasValue)
        {
            var headerPreview = table.Headers
                .Select(h => (h ?? string.Empty).Trim())
                .Take(30)
                .ToArray();
            var headerSummary = headerPreview.Length == 0
                ? "(none)"
                : string.Join(", ", headerPreview);
            throw new InvalidOperationException(
                $"File {path} is missing single coordinate columns. Expected x,y,z or File Name. " +
                $"Headers found (first {headerPreview.Length}): {headerSummary}");
        }

        var rows = new List<LocalizationRow>();
        foreach (var cols in table.Rows)
        {
            double[] channels = new double[FeatureBuilder.ChannelCount];
            for (int i = 0; i < FeatureBuilder.ChannelCount; i++)
            {
                channels[i] = ParseDouble(SafeGet(cols, channelIndexes[i]));
            }

            double? duration = durationIndex.HasValue
                ? ParseNullableDouble(SafeGet(cols, durationIndex.Value))
                : inferredDuration;

            var single = new double[3];
            if (hasExplicitCoords)
            {
                single[0] = ParseDouble(SafeGet(cols, xIndex!.Value));
                single[1] = ParseDouble(SafeGet(cols, yIndex!.Value));
                single[2] = ParseDouble(SafeGet(cols, zIndex!.Value));
            }
            else
            {
                var rawFileName = SafeGet(cols, fileNameIndex!.Value);
                if (!TryParseSingleCoordsFromFileName(rawFileName, out single[0], out single[1], out single[2]))
                {
                    Console.WriteLine($"Warning: Unable to parse coordinates from file name '{rawFileName}'. Skipping row.");
                    continue;
                }
            }
            rows.Add(new LocalizationRow
            {
                Channels = channels,
                DurationSeconds = duration,
                IsDual = false,
                SingleCoordinates = single
            });
        }

        return rows;
    }

    private static LocalizationRow CreateDualRow(string[] cols, int[] channelIndexes, int? durationIndex, double? inferredDuration, double[] dualCoords)
    {
        double[] channels = new double[FeatureBuilder.ChannelCount];
        for (int i = 0; i < FeatureBuilder.ChannelCount; i++)
        {
            channels[i] = ParseDouble(SafeGet(cols, channelIndexes[i]));
        }

        double? duration = durationIndex.HasValue
            ? ParseNullableDouble(SafeGet(cols, durationIndex.Value))
            : inferredDuration;

        return new LocalizationRow
        {
            Channels = channels,
            DurationSeconds = duration,
            IsDual = true,
            DualCoordinates = dualCoords
        };
    }

    private static int[] ResolveChannelIndexes(Table table, string path)
    {
        var missingChannels = new List<string>();
        var channelIndexes = new int[FeatureBuilder.ChannelCount];
        for (int i = 1; i <= FeatureBuilder.ChannelCount; i++)
        {
            var exactHeader = $"Channel{i}";
            var spacedHeader = $"Channel {i}";
            int? index = TryGetIndex(table.HeaderMap, exactHeader)
                ?? TryGetIndex(table.HeaderMap, spacedHeader);
            if (!index.HasValue)
            {
                missingChannels.Add(exactHeader);
                channelIndexes[i - 1] = -1;
                continue;
            }

            channelIndexes[i - 1] = index.Value;
        }

        if (missingChannels.Count > 0)
        {
            var headerPreview = table.Headers
                .Select(h => (h ?? string.Empty).Trim())
                .Take(30)
                .ToArray();
            var headerSummary = headerPreview.Length == 0
                ? "(none)"
                : string.Join(", ", headerPreview);
            throw new InvalidOperationException(
                $"File {path} is missing channel columns: {string.Join(", ", missingChannels)}. " +
                $"Headers found (first {headerPreview.Length}): {headerSummary}");
        }

        return channelIndexes;
    }

    private static int[] ResolveDualCoordinateIndexes(Table table, string path)
    {
        int? x1Index = TryGetIndex(table.HeaderMap, "x1");
        int? y1Index = TryGetIndex(table.HeaderMap, "y1");
        int? z1Index = TryGetIndex(table.HeaderMap, "z1");
        int? x2Index = TryGetIndex(table.HeaderMap, "x2");
        int? y2Index = TryGetIndex(table.HeaderMap, "y2");
        int? z2Index = TryGetIndex(table.HeaderMap, "z2");

        if (!x1Index.HasValue || !y1Index.HasValue || !z1Index.HasValue || !x2Index.HasValue || !y2Index.HasValue || !z2Index.HasValue)
        {
            throw new InvalidOperationException($"File {path} is missing one or more dual coordinate columns (x1,y1,z1,x2,y2,z2)");
        }

        return new[] { x1Index.Value, y1Index.Value, z1Index.Value, x2Index.Value, y2Index.Value, z2Index.Value };
    }

    private static double[] ParseDualCoords(string[] cols, int[] coordIndexes)
    {
        var dual = new double[6];
        for (int i = 0; i < coordIndexes.Length; i++)
        {
            dual[i] = ParseDouble(SafeGet(cols, coordIndexes[i]));
        }

        return dual;
    }

    private static Dictionary<string, double[]> BuildMetadataLookup(Table table, int[] coordIndexes, string joinKey)
    {
        var lookup = new Dictionary<string, double[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in table.Rows)
        {
            if (!TryGetValue(table, row, joinKey, out var key))
            {
                continue;
            }

            lookup[key] = ParseDualCoords(row, coordIndexes);
        }

        return lookup;
    }

    private static string? FindJoinKey(string[] countHeaders, string[] metadataHeaders)
    {
        var metadataSet = new HashSet<string>(metadataHeaders, StringComparer.OrdinalIgnoreCase);
        foreach (var key in PreferredJoinKeys)
        {
            if (countHeaders.Any(h => string.Equals(h, key, StringComparison.OrdinalIgnoreCase)) && metadataSet.Contains(key))
            {
                return key;
            }
        }

        return null;
    }

    private static bool TryGetValue(Table table, string[] row, string header, out string value)
    {
        value = string.Empty;
        if (!table.HeaderMap.TryGetValue(header, out var idx))
        {
            return false;
        }

        value = SafeGet(row, idx).Trim();
        return value.Length > 0;
    }

    private static string SafeGet(string[] cols, int index)
    {
        return index >= 0 && index < cols.Length ? cols[index] : string.Empty;
    }

    private static Table LoadTable(string path)
    {
        var extension = Path.GetExtension(path);
        if (!string.Equals(extension, ".csv", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException("Excel input not supported; use CSV.");
        }

        return LoadCsvTable(path);
    }

    private static Table LoadCsvTable(string path)
    {
        var lines = File.ReadAllLines(path);
        if (lines.Length == 0)
        {
            return new Table(Array.Empty<string>(), new List<string[]>());
        }

        var headers = lines[0].Split(',').Select(h => h.Trim()).ToArray();
        var rows = new List<string[]>();
        foreach (var line in lines.Skip(1))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            rows.Add(line.Split(','));
        }

        return new Table(headers, rows);
    }


    private static int? TryGetIndex(Dictionary<string, int> headerMap, string name)
    {
        return headerMap.TryGetValue(name, out var idx) ? idx : null;
    }

    private static double ParseDouble(string value)
    {
        return double.Parse(value, NumberStyles.Any, CultureInfo.InvariantCulture);
    }

    private static double? ParseNullableDouble(string value)
    {
        return double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var result) ? result : null;
    }

    private static bool TryParseSingleCoordsFromFileName(string fileName, out double x, out double y, out double z)
    {
        x = 0;
        y = 0;
        z = 0;
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return false;
        }

        var baseName = Path.GetFileName(fileName.Trim());
        var matches = Regex.Matches(baseName, @"[-+]?\d+(?:\.\d+)?", RegexOptions.CultureInvariant);
        if (matches.Count < 4)
        {
            return false;
        }

        if (!int.TryParse(matches[0].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
        {
            return false;
        }

        return double.TryParse(matches[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out x)
            && double.TryParse(matches[2].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out y)
            && double.TryParse(matches[3].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out z);
    }

    private static double? InferDurationFromName(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var segments = name.Split('_');
        foreach (var segment in segments)
        {
            if (double.TryParse(segment, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed))
            {
                return parsed;
            }
        }

        return null;
    }

    private sealed record Table(string[] Headers, List<string[]> Rows)
    {
        public Dictionary<string, int> HeaderMap { get; } = Headers
            .Select((h, idx) => (Header: h.Trim(), Index: idx))
            .ToDictionary(h => h.Header, h => h.Index, StringComparer.OrdinalIgnoreCase);
    }
}
