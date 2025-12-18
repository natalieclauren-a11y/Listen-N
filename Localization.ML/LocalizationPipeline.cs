using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Numerics;
using Microsoft.ML;

namespace Localization.ML;

public sealed class LocalizationPipeline
{
    private readonly MLContext _mlContext;
    private readonly FeatureBuilder _featureBuilder;
    private readonly ITransformer _classifier;
    private readonly RegressionModelGroup _singleRegressor;
    private readonly RegressionModelGroup _dualRegressor;
    private readonly MahalanobisScorer _mahalanobis;
    private readonly PipelineConfiguration _config;

    public LocalizationPipeline(MLContext mlContext, FeatureBuilder featureBuilder, ITransformer classifier, RegressionModelGroup singleRegressor, RegressionModelGroup dualRegressor, MahalanobisScorer mahalanobis, PipelineConfiguration config)
    {
        _mlContext = mlContext;
        _featureBuilder = featureBuilder;
        _classifier = classifier;
        _singleRegressor = singleRegressor;
        _dualRegressor = dualRegressor;
        _mahalanobis = mahalanobis;
        _config = config;
    }

    public PredictionResult Predict(LocalizationRow row)
    {
        var features = _featureBuilder.BuildFeatures(row.Channels, row.DurationSeconds);
        _featureBuilder.EnsureFeatureParity(features.FeatureVector);
        var subset = ExtractOodFeatures(features.FeatureVector);
        double distance = _mahalanobis.Score(subset);
        bool isOod = distance > _config.OutOfDistributionThreshold;

        var classificationEngine = _mlContext.Model.CreatePredictionEngine<ClassificationExample, ClassificationPrediction>(_classifier);
        var classPrediction = classificationEngine.Predict(new ClassificationExample
        {
            Features = features.FeatureVector.Select(f => (float)f).ToArray()
        });

        string label = classPrediction.PredictedLabel ? "Dual" : "Single";
        double[] coords = classPrediction.PredictedLabel
            ? _dualRegressor.Predict(features.FeatureVector.Select(f => (float)f).ToArray())
            : _singleRegressor.Predict(features.FeatureVector.Select(f => (float)f).ToArray());

        if (classPrediction.PredictedLabel)
        {
            var first = coords.Take(3).ToArray();
            var second = coords.Skip(3).Take(3).ToArray();
            double separation = Math.Sqrt(first.Zip(second).Sum(p => Math.Pow(p.First - p.Second, 2)));
            if (separation < _config.MinimumSeparationCm && classPrediction.Probability < _config.StrictProbability)
            {
                coords = _singleRegressor.Predict(features.FeatureVector.Select(f => (float)f).ToArray());
                label = "Single (centroid override)";
            }
        }

        return new PredictionResult
        {
            Label = isOod ? "Unknown" : label,
            Coordinates = coords,
            Probability = classPrediction.Probability,
            RawClassification = classPrediction,
            Diagnostics = new PredictionDiagnostics
            {
                MahalanobisDistance = distance,
                IsOutOfDistribution = isOod,
                FeatureNames = _featureBuilder.FeatureNames
            }
        };
    }

    public void Save(string directory)
    {
        Directory.CreateDirectory(directory);
        using (var fs = File.Create(Path.Combine(directory, "classifier.zip")))
        {
            _mlContext.Model.Save(_classifier, inputSchema: null, stream: fs);
        }

        _singleRegressor.Save(Path.Combine(directory, "single_regressor"));
        _dualRegressor.Save(Path.Combine(directory, "dual_regressor"));

        var config = _config;
        config.FeatureNames = _featureBuilder.FeatureNames;
        config.DipolePositions = _featureBuilder.DipolePositions;
        var json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(directory, "pipeline_config.json"), json);

        var inv = _mahalanobis.InverseCovariance;
        var mahaModel = new MahalanobisModel
        {
            Mean = _mahalanobis.Mean,
            InverseCovariance = new List<IReadOnlyList<double>>
            {
                new [] { (double)inv.M11, (double)inv.M12, (double)inv.M13, (double)inv.M14 },
                new [] { (double)inv.M21, (double)inv.M22, (double)inv.M23, (double)inv.M24 },
                new [] { (double)inv.M31, (double)inv.M32, (double)inv.M33, (double)inv.M34 },
                new [] { (double)inv.M41, (double)inv.M42, (double)inv.M43, (double)inv.M44 }
            }
        };
        File.WriteAllText(Path.Combine(directory, "mahalanobis.json"), JsonSerializer.Serialize(mahaModel, new JsonSerializerOptions { WriteIndented = true }));
    }

    public static LocalizationPipeline Load(string directory, MLContext? mlContext = null)
    {
        mlContext ??= new MLContext(seed: 42);

        var configText = File.ReadAllText(Path.Combine(directory, "pipeline_config.json"));
        var config = JsonSerializer.Deserialize<PipelineConfiguration>(configText) ?? throw new InvalidOperationException("Missing pipeline configuration");
        var featureBuilder = new FeatureBuilder(config.Epsilon, config.DipolePositions);

        using var fs = File.OpenRead(Path.Combine(directory, "classifier.zip"));
        var classifier = mlContext.Model.Load(fs, out _);
        var singleRegressor = RegressionModelGroup.Load(mlContext, Path.Combine(directory, "single_regressor"));
        var dualRegressor = RegressionModelGroup.Load(mlContext, Path.Combine(directory, "dual_regressor"));

        var mahalanobis = new MahalanobisScorer(new double[4] { 0, 0, 0, 0 }, Matrix4x4.Identity);
        if (File.Exists(Path.Combine(directory, "mahalanobis.json")))
        {
            var mjson = File.ReadAllText(Path.Combine(directory, "mahalanobis.json"));
            var model = JsonSerializer.Deserialize<MahalanobisModel>(mjson);
            if (model != null)
            {
                var matrix = new Matrix4x4(
                    (float)model.InverseCovariance[0][0], (float)model.InverseCovariance[0][1], (float)model.InverseCovariance[0][2], (float)model.InverseCovariance[0][3],
                    (float)model.InverseCovariance[1][0], (float)model.InverseCovariance[1][1], (float)model.InverseCovariance[1][2], (float)model.InverseCovariance[1][3],
                    (float)model.InverseCovariance[2][0], (float)model.InverseCovariance[2][1], (float)model.InverseCovariance[2][2], (float)model.InverseCovariance[2][3],
                    (float)model.InverseCovariance[3][0], (float)model.InverseCovariance[3][1], (float)model.InverseCovariance[3][2], (float)model.InverseCovariance[3][3]
                );
                mahalanobis = new MahalanobisScorer(model.Mean, matrix);
            }
        }

        return new LocalizationPipeline(mlContext, featureBuilder, classifier, singleRegressor, dualRegressor, mahalanobis, config);
    }

    private static double[] ExtractOodFeatures(IReadOnlyList<double> features)
    {
        // positions: entropy, gini, anisotropy, dipole = after raw+total+normalized => indexes
        int offset = FeatureBuilder.ChannelCount + 1 + FeatureBuilder.ChannelCount;
        return new[]
        {
            features[offset],
            features[offset + 1],
            features[offset + 2],
            features[offset + 3]
        };
    }
}

public sealed class MahalanobisModel
{
    public required IReadOnlyList<double> Mean { get; init; }
    public required IReadOnlyList<IReadOnlyList<double>> InverseCovariance { get; init; }
}
