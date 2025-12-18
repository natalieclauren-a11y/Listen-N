using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.ML;

namespace Localization.ML;

public sealed class RegressionModelGroup
{
    private readonly Dictionary<string, ITransformer> _models;
    private readonly MLContext _mlContext;
    public IReadOnlyList<string> TargetNames { get; }

    public RegressionModelGroup(MLContext mlContext, Dictionary<string, ITransformer> models)
    {
        _mlContext = mlContext;
        _models = models;
        TargetNames = models.Keys.ToList();
    }

    public double[] Predict(float[] features)
    {
        var outputs = new double[TargetNames.Count];
        int idx = 0;
        foreach (var kvp in _models)
        {
            var engine = _mlContext.Model.CreatePredictionEngine<RegressionExample, RegressionPrediction>(kvp.Value);
            outputs[idx++] = engine.Predict(new RegressionExample { Features = features }).Score;
        }

        return outputs;
    }

    public void Save(string directory)
    {
        Directory.CreateDirectory(directory);
        foreach (var kvp in _models)
        {
            var path = Path.Combine(directory, $"reg_{kvp.Key}.zip");
            using var fs = File.Create(path);
            _mlContext.Model.Save(kvp.Value, inputSchema: null, stream: fs);
        }
    }

    public static RegressionModelGroup Load(MLContext mlContext, string directory)
    {
        var models = new Dictionary<string, ITransformer>();
        foreach (var file in Directory.GetFiles(directory, "reg_*.zip"))
        {
            using var fs = File.OpenRead(file);
            models[Path.GetFileNameWithoutExtension(file)[4..]] = mlContext.Model.Load(fs, out _);
        }

        return new RegressionModelGroup(mlContext, models);
    }
}
