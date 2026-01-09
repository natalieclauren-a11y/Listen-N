using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Localization.ML;

public sealed record PipelineIdentity
{
    public required string SchemaHash { get; init; }
    public required string FeatureHash { get; init; }
    public required int TrainingDurationSec { get; init; }
    public required string TrainingDatasetFingerprint { get; init; }
    public required string BuildTimestampUtc { get; init; }
    public string? CommitHash { get; init; }

    public static PipelineIdentity Load(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new ArgumentException("Artifacts directory is required.", nameof(directory));
        }

        string identityPath = Path.Combine(directory, "pipeline_identity.json");
        if (!File.Exists(identityPath))
        {
            throw new LocalizationPipelineIdentityException(LocalizationPipelineIdentityFailureReason.Missing, $"Pipeline identity file missing at {identityPath}.");
        }

        try
        {
            var json = File.ReadAllText(identityPath);
            var identity = JsonSerializer.Deserialize<PipelineIdentity>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            if (identity is null
                || string.IsNullOrWhiteSpace(identity.SchemaHash)
                || string.IsNullOrWhiteSpace(identity.FeatureHash)
                || identity.TrainingDurationSec <= 0
                || string.IsNullOrWhiteSpace(identity.TrainingDatasetFingerprint)
                || string.IsNullOrWhiteSpace(identity.BuildTimestampUtc))
            {
                throw new LocalizationPipelineIdentityException(LocalizationPipelineIdentityFailureReason.Invalid, "Pipeline identity file missing required fields.");
            }

            return identity;
        }
        catch (LocalizationPipelineIdentityException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new LocalizationPipelineIdentityException(LocalizationPipelineIdentityFailureReason.Invalid, "Failed to parse pipeline identity file.", ex);
        }
    }

    public static string ComputeModelId(PipelineIdentity identity)
    {
        var json = JsonSerializer.Serialize(identity, new JsonSerializerOptions
        {
            WriteIndented = false
        });
        using var sha = SHA256.Create();
        var bytes = Encoding.UTF8.GetBytes(json);
        var hash = sha.ComputeHash(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}

public sealed class LocalizationPipelineIdentityException : Exception
{
    public LocalizationPipelineIdentityFailureReason Reason { get; }

    public LocalizationPipelineIdentityException(LocalizationPipelineIdentityFailureReason reason, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Reason = reason;
    }
}

public enum LocalizationPipelineIdentityFailureReason
{
    Missing,
    Invalid
}
