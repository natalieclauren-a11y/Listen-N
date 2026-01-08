using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Integrated.Runtime;
using Localization.ML;
using Microsoft.ML;
using Xunit;

namespace Listen_N.Tests;

public sealed class LocalizationArtifactsLoaderTests
{
    [Fact]
    public void LoaderRejectsMismatchedSchemaHash()
    {
        using var tempDir = Directory.CreateTempSubdirectory();
        var artifactsDir = tempDir.FullName;
        var policyPath = WritePolicy(artifactsDir, thresholds30: 100, thresholds60: 200);
        WriteMinimalPipelineArtifacts(artifactsDir, out var config, out var runtimeHash);

        var manifest = BuildManifest(policyPath, config.SchemaVersion, "bad-hash");
        WriteManifest(artifactsDir, manifest);

        var ex = Assert.Throws<LocalizationArtifactsException>(() => LocalizationArtifactsLoader.Load(artifactsDir));
        Assert.Equal(LocalizationArtifactsFailureReason.SchemaMismatch, ex.Reason);
        Assert.Equal("bad-hash", ex.ExpectedHash);
        Assert.Equal(runtimeHash, ex.ActualHash);
    }

    [Fact]
    public void LoaderRejectsMissingPolicyFile()
    {
        using var tempDir = Directory.CreateTempSubdirectory();
        var artifactsDir = tempDir.FullName;
        WriteMinimalPipelineArtifacts(artifactsDir, out var config, out _);

        var manifest = BuildManifest("trigger_policy.json", config.SchemaVersion, config.SchemaHash);
        WriteManifest(artifactsDir, manifest);

        var ex = Assert.Throws<LocalizationArtifactsException>(() => LocalizationArtifactsLoader.Load(artifactsDir));
        Assert.Equal(LocalizationArtifactsFailureReason.PolicyMissing, ex.Reason);
    }

    [Fact]
    public void LoaderAcceptsManifestAndPolicy()
    {
        using var tempDir = Directory.CreateTempSubdirectory();
        var artifactsDir = tempDir.FullName;
        var policyPath = WritePolicy(artifactsDir, thresholds30: 123, thresholds60: 456);
        WriteMinimalPipelineArtifacts(artifactsDir, out var config, out _);

        var manifest = BuildManifest(policyPath, config.SchemaVersion, config.SchemaHash);
        WriteManifest(artifactsDir, manifest);

        var artifacts = LocalizationArtifactsLoader.Load(artifactsDir);
        Assert.Equal(123, artifacts.Thresholds.Nmin_15cm_30s);
        Assert.Equal(456, artifacts.Thresholds.Nmin_15cm_60s);
        Assert.Equal(config.SchemaHash, artifacts.Manifest.SchemaHash);
    }

    private static void WriteMinimalPipelineArtifacts(string artifactsDir, out PipelineConfiguration config, out string runtimeHash)
    {
        var mlContext = new MLContext(seed: 1);
        var featureBuilder = new FeatureBuilder();
        var featureNames = featureBuilder.FeatureNames;
        var exampleFeatures = new float[FeatureBuilder.ExpectedFeatureCount];

        var classifierData = mlContext.Data.LoadFromEnumerable(new[]
        {
            new ClassificationExample { Label = true, Features = exampleFeatures },
            new ClassificationExample { Label = false, Features = exampleFeatures }
        });

        var classifier = mlContext.BinaryClassification.Trainers.SdcaLogisticRegression().Fit(classifierData);
        using (var fs = File.Create(Path.Combine(artifactsDir, "classifier.zip")))
        {
            mlContext.Model.Save(classifier, classifierData.Schema, fs);
        }

        WriteRegressorGroup(mlContext, Path.Combine(artifactsDir, "single_regressor"));
        WriteRegressorGroup(mlContext, Path.Combine(artifactsDir, "dual_regressor"));

        config = new PipelineConfiguration
        {
            Epsilon = featureBuilder.Epsilon,
            OutOfDistributionThreshold = 1.0,
            ClassifierThreshold = 0.5,
            MinimumSeparationCm = 8,
            StrictProbability = 0.98,
            SchemaVersion = SchemaStampBuilder.DefaultSchemaVersion,
            FeatureNames = featureNames,
            FeatureColumns = featureNames,
            DipolePositions = featureBuilder.DipolePositions
        };

        var runtimeStamp = new FeatureSchemaStamp
        {
            SchemaVersion = config.SchemaVersion,
            FeatureNames = featureBuilder.FeatureNames,
            FeatureColumns = config.FeatureColumns,
            Epsilon = featureBuilder.Epsilon,
            ChannelCount = FeatureBuilder.ChannelCount,
            DipolePositions = featureBuilder.DipolePositions
        };
        runtimeHash = FeatureSchemaHasher.ComputeHash(runtimeStamp);
        config.SchemaHash = runtimeHash;

        File.WriteAllText(Path.Combine(artifactsDir, "pipeline_config.json"),
            JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void WriteRegressorGroup(MLContext mlContext, string directory)
    {
        Directory.CreateDirectory(directory);
        var data = mlContext.Data.LoadFromEnumerable(new[]
        {
            new RegressionExample { Label = 1.0f, Features = new float[FeatureBuilder.ExpectedFeatureCount] },
            new RegressionExample { Label = 2.0f, Features = new float[FeatureBuilder.ExpectedFeatureCount] }
        });
        var model = mlContext.Regression.Trainers.Sdca().Fit(data);
        using var fs = File.Create(Path.Combine(directory, "reg_x.zip"));
        mlContext.Model.Save(model, data.Schema, fs);
    }

    private static string WritePolicy(string artifactsDir, int thresholds30, int thresholds60)
    {
        var doc = new TriggerPolicyDocument
        {
            Nmin_15cm_30s = thresholds30,
            Nmin_15cm_60s = thresholds60,
            ConfuseDebounceWindows = 2,
            RecoverDebounceWindows = 2,
            QualityMin = 3.0,
            CheckEveryCounts = 2000,
            MinPublishDurationSeconds = 30.0,
            MaxPublishDurationSeconds = 60.0,
            ThrashWindowCount = 6,
            ThrashChangeThreshold = 3,
            StabilityToleranceCm = 5.0,
            StabilityK = 3,
            EarlyStopProbability = 0.98,
            EarlyStopK = 2,
            PublishProbabilityMin = 0.80
        };

        string policyPath = Path.Combine(artifactsDir, "trigger_policy.json");
        File.WriteAllText(policyPath, JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true }));
        return Path.GetFileName(policyPath);
    }

    private static LocalizationArtifactManifest BuildManifest(string policyPath, string schemaVersion, string schemaHash)
    {
        return new LocalizationArtifactManifest
        {
            SchemaVersion = schemaVersion,
            SchemaHash = schemaHash,
            TrainingDurationsSeconds = new[] { 30, 60 },
            TriggerPolicyPath = policyPath,
            ModelPaths = new Dictionary<string, string>
            {
                ["classifier"] = "classifier.zip",
                ["single_regressor"] = "single_regressor",
                ["dual_regressor"] = "dual_regressor",
                ["pipeline_config"] = "pipeline_config.json"
            }
        };
    }

    private static void WriteManifest(string artifactsDir, LocalizationArtifactManifest manifest)
    {
        var json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(artifactsDir, "manifest.json"), json);
    }
}
