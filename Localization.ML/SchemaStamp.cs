using System;
using System.Buffers;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text.Json;

namespace Localization.ML;

public sealed class SchemaStamp
{
    public required string SchemaVersion { get; init; }
    public string? CodeVersion { get; init; }
    public IReadOnlyList<string> FeatureSpec { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> FeatureColumns { get; init; } = Array.Empty<string>();
    public string ClassifierTrainer { get; init; } = string.Empty;
    public IReadOnlyList<string> RegressorTrainers { get; init; } = Array.Empty<string>();
    public double ProbabilityThreshold { get; init; }
    public double OutOfDistributionThreshold { get; init; }
    public double MinimumSeparationCm { get; init; }
    public double Epsilon { get; init; }
    public int ChannelCount { get; init; }
    public IReadOnlyList<double> DipolePositions { get; init; } = Array.Empty<double>();
}

public static class SchemaStampBuilder
{
    public const string DefaultSchemaVersion = "v1";

    public static SchemaStamp Build(PipelineConfiguration config, FeatureBuilder featureBuilder, string classifierTrainer, IReadOnlyList<string> regressorTrainers)
    {
        var featureColumns = config.FeatureColumns.Count == 0 ? featureBuilder.FeatureNames : config.FeatureColumns;
        return new SchemaStamp
        {
            SchemaVersion = string.IsNullOrWhiteSpace(config.SchemaVersion) ? DefaultSchemaVersion : config.SchemaVersion!,
            CodeVersion = typeof(SchemaStampBuilder).Assembly.GetName().Version?.ToString(),
            FeatureSpec = featureBuilder.FeatureNames,
            FeatureColumns = featureColumns,
            ClassifierTrainer = classifierTrainer,
            RegressorTrainers = regressorTrainers,
            ProbabilityThreshold = config.StrictProbability,
            OutOfDistributionThreshold = config.OutOfDistributionThreshold,
            MinimumSeparationCm = config.MinimumSeparationCm,
            Epsilon = featureBuilder.Epsilon,
            ChannelCount = FeatureBuilder.ChannelCount,
            DipolePositions = featureBuilder.DipolePositions
        };
    }

    public static string ComputeHash(SchemaStamp stamp)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false }))
        {
            WriteCanonical(writer, stamp);
        }

        var hash = SHA256.HashData(buffer.WrittenSpan);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static void WriteCanonical(Utf8JsonWriter writer, SchemaStamp stamp)
    {
        writer.WriteStartObject();
        writer.WriteString("SchemaVersion", stamp.SchemaVersion);
        if (!string.IsNullOrWhiteSpace(stamp.CodeVersion))
        {
            writer.WriteString("CodeVersion", stamp.CodeVersion);
        }

        WriteStringArray(writer, "FeatureSpec", stamp.FeatureSpec);
        WriteStringArray(writer, "FeatureColumns", stamp.FeatureColumns);
        writer.WriteString("ClassifierTrainer", stamp.ClassifierTrainer);
        WriteStringArray(writer, "RegressorTrainers", stamp.RegressorTrainers);
        writer.WriteNumber("ProbabilityThreshold", stamp.ProbabilityThreshold);
        writer.WriteNumber("OutOfDistributionThreshold", stamp.OutOfDistributionThreshold);
        writer.WriteNumber("MinimumSeparationCm", stamp.MinimumSeparationCm);
        writer.WriteNumber("Epsilon", stamp.Epsilon);
        writer.WriteNumber("ChannelCount", stamp.ChannelCount);
        WriteNumberArray(writer, "DipolePositions", stamp.DipolePositions);
        writer.WriteEndObject();
    }

    private static void WriteStringArray(Utf8JsonWriter writer, string propertyName, IReadOnlyList<string> values)
    {
        writer.WritePropertyName(propertyName);
        writer.WriteStartArray();
        foreach (var value in values)
        {
            writer.WriteStringValue(value);
        }

        writer.WriteEndArray();
    }

    private static void WriteNumberArray(Utf8JsonWriter writer, string propertyName, IReadOnlyList<double> values)
    {
        writer.WritePropertyName(propertyName);
        writer.WriteStartArray();
        foreach (var value in values)
        {
            writer.WriteNumberValue(value);
        }

        writer.WriteEndArray();
    }
}
