using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.ML;
using Microsoft.ML.Data;
using Microsoft.ML.Trainers;
using Microsoft.ML.Trainers.FastTree;


namespace Localization.ML;

public sealed class ModelTrainer
{
    private readonly MLContext _mlContext;
    private readonly FeatureBuilder _featureBuilder;

    public ModelTrainer(int seed = 42)
    {
        _mlContext = new MLContext(seed: seed);
        _featureBuilder = new FeatureBuilder();
    }

    public FeatureBuilder FeatureBuilder => _featureBuilder;
    public MLContext MlContext => _mlContext;

    public (ITransformer Model, BinaryClassificationMetrics Metrics, IReadOnlyList<(string Feature, double Gain)> Importances) TrainClassifier(IReadOnlyList<ClassificationExample> data)
    {
        var split = StratifiedSplit(data, 0.25);
        var trainData = _mlContext.Data.LoadFromEnumerable(split.Train);
        var testData = _mlContext.Data.LoadFromEnumerable(split.Test);

        var pipeline = _mlContext.BinaryClassification.Trainers.FastForest(new Microsoft.ML.Trainers.FastTree.FastForestBinaryTrainer.Options

        {
            NumberOfTrees = 200,
            NumberOfLeaves = 64,
            LabelColumnName = nameof(ClassificationExample.Label),
            FeatureColumnName = nameof(ClassificationExample.Features)
        });

        var model = pipeline.Fit(trainData);
        var predictions = model.Transform(testData);
        var metrics = _mlContext.BinaryClassification.Evaluate(predictions, labelColumnName: nameof(ClassificationExample.Label));

        var importances = _mlContext.BinaryClassification.PermutationFeatureImportance(model, testData, permutationCount: 10)
            .Select((metrics, idx) => (Feature: _featureBuilder.FeatureNames[idx], Gain: metrics.AreaUnderRocCurve.Mean))
            .OrderByDescending(x => x.Gain)
            .ToList();

        return (model, metrics, importances);
    }

    public (double Mean, double Std) CrossValidateClassifier(IReadOnlyList<ClassificationExample> data)
    {
        var dataView = _mlContext.Data.LoadFromEnumerable(data);

        var estimator = _mlContext.BinaryClassification.Trainers.FastForest(
            new Microsoft.ML.Trainers.FastTree.FastForestBinaryTrainer.Options
            {
                NumberOfTrees = 200,
                NumberOfLeaves = 64,
                LabelColumnName = nameof(ClassificationExample.Label),
                FeatureColumnName = nameof(ClassificationExample.Features)
            });

        var results = _mlContext.BinaryClassification.CrossValidate(
            data: dataView,
            estimator: estimator,
            numberOfFolds: 5,
            labelColumnName: nameof(ClassificationExample.Label));

        double mean = results.Average(r => r.Metrics.Accuracy);
        double std = Math.Sqrt(results.Average(r => Math.Pow(r.Metrics.Accuracy - mean, 2)));
        return (mean, std);
    }


    public double RandomLabelSanityCheck(IReadOnlyList<ClassificationExample> data)
    {
        var rnd = new Random(123);
        var shuffled = data.Select(d => new ClassificationExample
        {
            Features = d.Features,
            Label = rnd.NextDouble() > 0.5
        }).ToList();

        var metrics = CrossValidateClassifier(shuffled);
        return metrics.Mean;
    }

    public RegressionModelGroup TrainRegressor(IReadOnlyList<(float[] Features, float Label)> rows, string targetName)
    {
        var data = rows.Select(r => new RegressionExample { Features = r.Features, Label = r.Label });
        var dataView = _mlContext.Data.LoadFromEnumerable(data);
        var pipeline = _mlContext.Regression.Trainers.FastForest(new Microsoft.ML.Trainers.FastTree.FastForestRegressionTrainer.Options

        {
            NumberOfTrees = 200,
            NumberOfLeaves = 128,
            LabelColumnName = nameof(RegressionExample.Label),
            FeatureColumnName = nameof(RegressionExample.Features)
        });

        var model = pipeline.Fit(dataView);
        return new RegressionModelGroup(_mlContext, new Dictionary<string, ITransformer> { { targetName, model } });
    }

    public RegressionModelGroup TrainMultiRegressor(IReadOnlyList<float[]> features, IReadOnlyList<double[]> targets, IReadOnlyList<string> targetNames)
    {
        var models = new Dictionary<string, ITransformer>();
        for (int targetIdx = 0; targetIdx < targetNames.Count; targetIdx++)
        {
            var rows = features.Zip(targets, (f, t) => new RegressionExample { Features = f, Label = (float)t[targetIdx] });
            var dataView = _mlContext.Data.LoadFromEnumerable(rows);
            var pipeline = _mlContext.Regression.Trainers.FastForest(new FastForestRegressionTrainer.Options
            {
                NumberOfTrees = 200,
                NumberOfLeaves = 128,
                LabelColumnName = nameof(RegressionExample.Label),
                FeatureColumnName = nameof(RegressionExample.Features)
            });
            models[targetNames[targetIdx]] = pipeline.Fit(dataView);
        }

        return new RegressionModelGroup(_mlContext, models);
    }

    public RegressionMetrics EvaluateRegressor(ITransformer model, IReadOnlyList<(float[] Features, float Label)> rows)
    {
        var data = _mlContext.Data.LoadFromEnumerable(rows.Select(r => new RegressionExample { Features = r.Features, Label = r.Label }));
        var transformed = model.Transform(data);
        return _mlContext.Regression.Evaluate(transformed, labelColumnName: nameof(RegressionExample.Label));
    }

    private static (IReadOnlyList<ClassificationExample> Train, IReadOnlyList<ClassificationExample> Test) StratifiedSplit(IReadOnlyList<ClassificationExample> data, double testFraction)
    {
        var grouped = data.GroupBy(d => d.Label).ToDictionary(g => g.Key, g => g.ToList());
        var train = new List<ClassificationExample>();
        var test = new List<ClassificationExample>();
        var rnd = new Random(42);

        foreach (var kvp in grouped)
        {
            int testCount = (int)Math.Round(kvp.Value.Count * testFraction);
            var shuffled = kvp.Value.OrderBy(_ => rnd.Next()).ToList();
            test.AddRange(shuffled.Take(testCount));
            train.AddRange(shuffled.Skip(testCount));
        }

        return (train, test);
    }
}
