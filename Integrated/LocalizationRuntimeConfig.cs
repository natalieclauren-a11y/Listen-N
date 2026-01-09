using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Integrated.Runtime
{
    public sealed record LocalizationRuntimeConfig
    {
        public required string ArtifactsDirectory { get; init; }
        public required string TriggerPolicyPath { get; init; }
        public required IReadOnlyList<string> AllowedSchemaHashes { get; init; }
        public required IReadOnlyList<int> SupportedTrainingDurationsSec { get; init; }
        public required int MaxMlQueueDepth { get; init; }
        public required int MaxMlRequestsPerEpisode { get; init; }
        public required bool DecisionLoggingEnabled { get; init; }
        public required bool FailFastOnStartupError { get; init; }
        public int DegradedRefusalThreshold { get; init; } = 3;
        public int DegradedQueueSaturationThreshold { get; init; } = 3;

        public static LocalizationRuntimeConfig Load(string configPath)
        {
            if (string.IsNullOrWhiteSpace(configPath))
            {
                throw new ArgumentException("Runtime config path is required.", nameof(configPath));
            }

            var json = File.ReadAllText(configPath);
            var config = JsonSerializer.Deserialize<LocalizationRuntimeConfig>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            }) ?? throw new InvalidOperationException("Localization runtime config missing or invalid.");

            return config;
        }

        public LocalizationRuntimeValidationResult Validate(string? baseDirectory = null)
        {
            var errors = new List<string>();

            string artifactsDirectory = ResolvePath(ArtifactsDirectory, baseDirectory);
            if (!Directory.Exists(artifactsDirectory))
            {
                errors.Add($"Artifacts directory not found at {artifactsDirectory}.");
            }

            string triggerPolicyPath = ResolvePath(TriggerPolicyPath, baseDirectory, artifactsDirectory);
            TriggerPolicyDocument? policy = null;
            if (!File.Exists(triggerPolicyPath))
            {
                errors.Add($"Trigger policy not found at {triggerPolicyPath}.");
            }
            else
            {
                try
                {
                    var json = File.ReadAllText(triggerPolicyPath);
                    policy = JsonSerializer.Deserialize<TriggerPolicyDocument>(json, new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    });
                    if (policy is null)
                    {
                        errors.Add("Trigger policy deserialized to null.");
                    }
                    else
                    {
                        policy.ToPolicyInputs();
                    }
                }
                catch (Exception ex)
                {
                    errors.Add($"Trigger policy invalid: {ex.Message}");
                }
            }

            if (SupportedTrainingDurationsSec is null || SupportedTrainingDurationsSec.Count == 0)
            {
                errors.Add("SupportedTrainingDurationsSec must include at least one duration.");
            }
            else if (policy is not null)
            {
                foreach (var duration in SupportedTrainingDurationsSec)
                {
                    if (duration == 30 && policy.Nmin_15cm_30s <= 0)
                    {
                        errors.Add("Trigger policy missing threshold for 30s duration.");
                    }
                    else if (duration == 60 && policy.Nmin_15cm_60s <= 0)
                    {
                        errors.Add("Trigger policy missing threshold for 60s duration.");
                    }
                    else if (duration != 30 && duration != 60)
                    {
                        errors.Add($"Unsupported training duration {duration}.");
                    }
                }
            }

            if (MaxMlQueueDepth <= 0)
            {
                errors.Add("MaxMlQueueDepth must be positive.");
            }

            if (MaxMlRequestsPerEpisode <= 0)
            {
                errors.Add("MaxMlRequestsPerEpisode must be positive.");
            }

            if (AllowedSchemaHashes is null || AllowedSchemaHashes.Count == 0)
            {
                errors.Add("AllowedSchemaHashes must include at least one schema hash.");
            }
            else if (Directory.Exists(artifactsDirectory))
            {
                string manifestPath = Path.Combine(artifactsDirectory, "manifest.json");
                if (!File.Exists(manifestPath))
                {
                    errors.Add($"Artifact manifest not found at {manifestPath}.");
                }
                else
                {
                    try
                    {
                        var manifestJson = File.ReadAllText(manifestPath);
                        var manifest = JsonSerializer.Deserialize<LocalizationArtifactManifest>(manifestJson);
                        if (manifest is null || string.IsNullOrWhiteSpace(manifest.SchemaHash))
                        {
                            errors.Add("Artifact manifest schema hash missing.");
                        }
                        else if (!AllowedSchemaHashes.Contains(manifest.SchemaHash, StringComparer.OrdinalIgnoreCase))
                        {
                            errors.Add($"Schema hash {manifest.SchemaHash} is not allowed.");
                        }
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"Artifact manifest invalid: {ex.Message}");
                    }
                }
            }

            return new LocalizationRuntimeValidationResult
            {
                IsValid = errors.Count == 0,
                Errors = errors,
                ResolvedArtifactsDirectory = artifactsDirectory,
                ResolvedTriggerPolicyPath = triggerPolicyPath
            };
        }

        private static string ResolvePath(string path, string? baseDirectory, string? artifactsDirectory = null)
        {
            if (Path.IsPathRooted(path))
            {
                return path;
            }

            if (!string.IsNullOrWhiteSpace(artifactsDirectory))
            {
                return Path.Combine(artifactsDirectory, path);
            }

            if (!string.IsNullOrWhiteSpace(baseDirectory))
            {
                return Path.Combine(baseDirectory, path);
            }

            return path;
        }
    }

    public sealed record LocalizationRuntimeValidationResult
    {
        public required bool IsValid { get; init; }
        public required IReadOnlyList<string> Errors { get; init; }
        public string? ResolvedArtifactsDirectory { get; init; }
        public string? ResolvedTriggerPolicyPath { get; init; }
    }
}
