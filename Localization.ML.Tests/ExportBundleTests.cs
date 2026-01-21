using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Localization.ML;
using Localization.Train;
using Microsoft.ML;
using Xunit;

namespace Localization.ML.Tests;

public sealed class ExportBundleTests
{
    [Fact]
    public void ExportBundle_WritesBundleArtifacts()
    {
        var tempDir = Directory.CreateTempSubdirectory();
        try
        {
            string inputDir = Path.Combine(tempDir.FullName, "input");
            string outputDir = Path.Combine(tempDir.FullName, "bundle");
            Directory.CreateDirectory(inputDir);

            var mlContext = new MLContext(seed: 1);
            var featureBuilder = new FeatureBuilder();

            string classifierPath = Path.Combine(inputDir, "classifier.zip");
            var classifierData = mlContext.Data.LoadFromEnumerable(new[]
            {
                new ClassificationExample { Label = true, Features = new float[FeatureBuilder.ExpectedFeatureCount] },
                new ClassificationExample { Label = false, Features = Enumerable.Repeat(1f, FeatureBuilder.ExpectedFeatureCount).ToArray() }
            });
            var classifier = mlContext.BinaryClassification.Trainers.SdcaLogisticRegression().Fit(classifierData);
            using (var fs = File.Create(classifierPath))
            {
                mlContext.Model.Save(classifier, inputSchema: null, stream: fs);
            }

            string singleDir = Path.Combine(inputDir, "single_regressor");
            string dualDir = Path.Combine(inputDir, "dual_regressor");
            BuildRegressorGroup(mlContext, new[] { "x", "y", "z" }).Save(singleDir);
            BuildRegressorGroup(mlContext, new[] { "x1", "y1", "z1", "x2", "y2", "z2" }).Save(dualDir);

            string configPath = Path.Combine(inputDir, "pipeline_config.json");
            var config = new PipelineConfiguration
            {
                Epsilon = featureBuilder.Epsilon,
                OutOfDistributionThreshold = 1.0,
                ClassifierThreshold = 0.5,
                MinimumSeparationCm = 1.0,
                StrictProbability = 0.5,
                FeatureNames = Array.Empty<string>(),
                FeatureColumns = Array.Empty<string>(),
                DipolePositions = Array.Empty<double>()
            };
            File.WriteAllText(configPath, System.Text.Json.JsonSerializer.Serialize(config, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));

            int exitCode = Program.RunExportBundle(new[]
            {
                "--out", outputDir,
                "--single", singleDir,
                "--dual", dualDir,
                "--classifier", classifierPath,
                "--config", configPath
            });

            Assert.Equal(0, exitCode);
            Assert.True(File.Exists(Path.Combine(outputDir, "pipeline_config.json")));
            Assert.True(File.Exists(Path.Combine(outputDir, "classifier.zip")));
            Assert.True(Directory.Exists(Path.Combine(outputDir, "single_regressor")));
            Assert.True(Directory.Exists(Path.Combine(outputDir, "dual_regressor")));
            Assert.True(File.Exists(Path.Combine(outputDir, "pipeline_identity.json")));
        }
        finally
        {
            if (tempDir.Exists)
            {
                tempDir.Delete(recursive: true);
            }
        }
    }

    private static RegressionModelGroup BuildRegressorGroup(MLContext mlContext, IEnumerable<string> targets)
    {
        var data = mlContext.Data.LoadFromEnumerable(new[]
        {
            new RegressionExample { Label = 1.0f, Features = new float[FeatureBuilder.ExpectedFeatureCount] },
            new RegressionExample { Label = 2.0f, Features = Enumerable.Repeat(1f, FeatureBuilder.ExpectedFeatureCount).ToArray() }
        });

        var models = new Dictionary<string, ITransformer>();
        foreach (var target in targets)
        {
            var model = mlContext.Regression.Trainers.Sdca().Fit(data);
            models[target] = model;
        }

        return new RegressionModelGroup(mlContext, models);
    }
}
