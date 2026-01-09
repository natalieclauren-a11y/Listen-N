using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Localization.ML;

namespace Integrated.Runtime;

public sealed record LocalizationArtifactManifest
{
    public required string SchemaVersion { get; init; }
    public required string SchemaHash { get; init; }
    public required IReadOnlyList<int> TrainingDurationsSeconds { get; init; }
    public required string TriggerPolicyPath { get; init; }
    public required IReadOnlyDictionary<string, string> ModelPaths { get; init; }
}

public sealed record LocalizationArtifacts
{
    public required LocalizationPipeline Pipeline { get; init; }
    public required TriggerPolicyThresholds Thresholds { get; init; }
    public required LocalizationEpisodePolicyConfig PolicyConfig { get; init; }
    public required LocalizationArtifactManifest Manifest { get; init; }
    public required string RuntimeSchemaHash { get; init; }
}

public enum LocalizationArtifactsFailureReason
{
    ManifestMissing,
    ManifestInvalid,
    SchemaMismatch,
    PolicyMissing,
    PolicyInvalid,
    IdentityMissing,
    IdentityInvalid,
    PipelineInvalid
}

public sealed class LocalizationArtifactsException : Exception
{
    public LocalizationArtifactsFailureReason Reason { get; }
    public string? ExpectedHash { get; }
    public string? ActualHash { get; }

    public LocalizationArtifactsException(
        LocalizationArtifactsFailureReason reason,
        string message,
        string? expectedHash = null,
        string? actualHash = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Reason = reason;
        ExpectedHash = expectedHash;
        ActualHash = actualHash;
    }
}

public static class LocalizationArtifactsLoader
{
    private const string ManifestFileName = "manifest.json";

    public static LocalizationArtifacts Load(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new ArgumentException("Artifacts directory is required.", nameof(directory));
        }

        string manifestPath = Path.Combine(directory, ManifestFileName);
        if (!File.Exists(manifestPath))
        {
            throw new LocalizationArtifactsException(
                LocalizationArtifactsFailureReason.ManifestMissing,
                $"Artifact manifest not found at {manifestPath}.");
        }

        LocalizationArtifactManifest manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<LocalizationArtifactManifest>(File.ReadAllText(manifestPath))
                ?? throw new InvalidOperationException("Manifest deserialized to null.");
        }
        catch (Exception ex)
        {
            throw new LocalizationArtifactsException(
                LocalizationArtifactsFailureReason.ManifestInvalid,
                "Failed to parse artifact manifest.",
                innerException: ex);
        }

        ValidateManifest(manifest, manifestPath, directory);

        var configPath = ResolveModelPath(directory, manifest, "pipeline_config", "pipeline_config.json");
        var configJson = File.ReadAllText(configPath);
        var config = JsonSerializer.Deserialize<PipelineConfiguration>(configJson)
            ?? throw new LocalizationArtifactsException(
                LocalizationArtifactsFailureReason.ManifestInvalid,
                "Pipeline configuration missing or invalid.");
        config.SchemaVersion = string.IsNullOrWhiteSpace(config.SchemaVersion)
            ? SchemaStampBuilder.DefaultSchemaVersion
            : config.SchemaVersion;
        if (config.FeatureColumns.Count == 0)
        {
            config.FeatureColumns = config.FeatureNames;
        }

        var runtimeHash = ComputeRuntimeSchemaHash(config);
        if (!string.Equals(manifest.SchemaVersion, config.SchemaVersion, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(manifest.SchemaHash, runtimeHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new LocalizationArtifactsException(
                LocalizationArtifactsFailureReason.SchemaMismatch,
                $"Schema mismatch. Manifest={manifest.SchemaHash}, Runtime={runtimeHash}.",
                expectedHash: manifest.SchemaHash,
                actualHash: runtimeHash);
        }

        string policyPath = Path.Combine(directory, manifest.TriggerPolicyPath);
        if (!File.Exists(policyPath))
        {
            throw new LocalizationArtifactsException(
                LocalizationArtifactsFailureReason.PolicyMissing,
                $"Trigger policy not found at {policyPath}.");
        }

        TriggerPolicyDocument policy;
        try
        {
            policy = JsonSerializer.Deserialize<TriggerPolicyDocument>(File.ReadAllText(policyPath), new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                })
                ?? throw new InvalidOperationException("Trigger policy deserialized to null.");
        }
        catch (Exception ex)
        {
            throw new LocalizationArtifactsException(
                LocalizationArtifactsFailureReason.PolicyInvalid,
                "Failed to parse trigger policy.",
                innerException: ex);
        }

        TriggerPolicyThresholds thresholds;
        LocalizationEpisodePolicyConfig policyConfig;
        try
        {
            (thresholds, policyConfig) = policy.ToPolicyInputs();
        }
        catch (Exception ex)
        {
            throw new LocalizationArtifactsException(
                LocalizationArtifactsFailureReason.PolicyInvalid,
                "Trigger policy values are invalid.",
                innerException: ex);
        }
        LocalizationPipeline pipeline;
        try
        {
            pipeline = LocalizationPipeline.Load(directory);
        }
        catch (LocalizationPipelineIdentityException ex)
        {
            var reason = ex.Reason == LocalizationPipelineIdentityFailureReason.Missing
                ? LocalizationArtifactsFailureReason.IdentityMissing
                : LocalizationArtifactsFailureReason.IdentityInvalid;
            throw new LocalizationArtifactsException(
                reason,
                ex.Message,
                innerException: ex);
        }
        catch (Exception ex)
        {
            throw new LocalizationArtifactsException(
                LocalizationArtifactsFailureReason.PipelineInvalid,
                "Failed to load localization pipeline.",
                innerException: ex);
        }

        return new LocalizationArtifacts
        {
            Pipeline = pipeline,
            Thresholds = thresholds,
            PolicyConfig = policyConfig,
            Manifest = manifest,
            RuntimeSchemaHash = runtimeHash
        };
    }

    private static void ValidateManifest(LocalizationArtifactManifest manifest, string manifestPath, string root)
    {
        if (string.IsNullOrWhiteSpace(manifest.SchemaVersion) || string.IsNullOrWhiteSpace(manifest.SchemaHash))
        {
            throw new LocalizationArtifactsException(
                LocalizationArtifactsFailureReason.ManifestInvalid,
                $"Manifest at {manifestPath} is missing schema metadata.");
        }

        if (string.IsNullOrWhiteSpace(manifest.TriggerPolicyPath))
        {
            throw new LocalizationArtifactsException(
                LocalizationArtifactsFailureReason.ManifestInvalid,
                $"Manifest at {manifestPath} is missing trigger policy path.");
        }

        if (manifest.TrainingDurationsSeconds is null || manifest.TrainingDurationsSeconds.Count == 0)
        {
            throw new LocalizationArtifactsException(
                LocalizationArtifactsFailureReason.ManifestInvalid,
                $"Manifest at {manifestPath} is missing training durations.");
        }

        if (!manifest.TrainingDurationsSeconds.Contains(30) || !manifest.TrainingDurationsSeconds.Contains(60))
        {
            throw new LocalizationArtifactsException(
                LocalizationArtifactsFailureReason.ManifestInvalid,
                $"Manifest at {manifestPath} must include training durations 30 and 60 seconds.");
        }

        if (manifest.ModelPaths is null || manifest.ModelPaths.Count == 0)
        {
            throw new LocalizationArtifactsException(
                LocalizationArtifactsFailureReason.ManifestInvalid,
                $"Manifest at {manifestPath} is missing model paths.");
        }

        var requiredKeys = new[] { "classifier", "single_regressor", "dual_regressor", "pipeline_config" };
        foreach (var key in requiredKeys)
        {
            if (!manifest.ModelPaths.ContainsKey(key))
            {
                throw new LocalizationArtifactsException(
                    LocalizationArtifactsFailureReason.ManifestInvalid,
                    $"Manifest at {manifestPath} is missing model path for '{key}'.");
            }

            ResolveModelPath(root, manifest, key, key switch
            {
                "classifier" => "classifier.zip",
                "single_regressor" => "single_regressor",
                "dual_regressor" => "dual_regressor",
                _ => "pipeline_config.json"
            });
        }
    }

    private static string ResolveModelPath(string root, LocalizationArtifactManifest manifest, string key, string fallback)
    {
        if (manifest.ModelPaths.TryGetValue(key, out var relative) && !string.IsNullOrWhiteSpace(relative))
        {
            var fullPath = Path.Combine(root, relative);
            if (!File.Exists(fullPath) && !Directory.Exists(fullPath))
            {
                throw new LocalizationArtifactsException(
                    LocalizationArtifactsFailureReason.ManifestInvalid,
                    $"Model path '{relative}' for '{key}' does not exist.");
            }

            return fullPath;
        }

        var fallbackPath = Path.Combine(root, fallback);
        if (!File.Exists(fallbackPath) && !Directory.Exists(fallbackPath))
        {
            throw new LocalizationArtifactsException(
                LocalizationArtifactsFailureReason.ManifestInvalid,
                $"Model path for '{key}' not found at {fallbackPath}.");
        }

        return fallbackPath;
    }

    private static string ComputeRuntimeSchemaHash(PipelineConfiguration config)
    {
        var featureBuilder = new FeatureBuilder(config.Epsilon, config.DipolePositions);
        var featureColumns = config.FeatureColumns.Count == 0 ? featureBuilder.FeatureNames : config.FeatureColumns;
        var stamp = new FeatureSchemaStamp
        {
            SchemaVersion = config.SchemaVersion,
            FeatureNames = featureBuilder.FeatureNames,
            FeatureColumns = featureColumns,
            Epsilon = featureBuilder.Epsilon,
            ChannelCount = FeatureBuilder.ChannelCount,
            DipolePositions = featureBuilder.DipolePositions
        };

        return FeatureSchemaHasher.ComputeHash(stamp);
    }
}
