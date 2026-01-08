using System;
using System.Buffers;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text.Json;

namespace Localization.ML;

public sealed class FeatureSchemaStamp
{
    public required string SchemaVersion { get; init; }
    public IReadOnlyList<string> FeatureNames { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> FeatureColumns { get; init; } = Array.Empty<string>();
    public double Epsilon { get; init; }
    public int ChannelCount { get; init; }
    public IReadOnlyList<double> DipolePositions { get; init; } = Array.Empty<double>();
}

public static class FeatureSchemaHasher
{
    public static string ComputeHash(FeatureSchemaStamp stamp)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false }))
        {
            WriteCanonical(writer, stamp);
        }

        var hash = SHA256.HashData(buffer.WrittenSpan);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static void WriteCanonical(Utf8JsonWriter writer, FeatureSchemaStamp stamp)
    {
        writer.WriteStartObject();
        writer.WriteString("SchemaVersion", stamp.SchemaVersion);
        WriteStringArray(writer, "FeatureNames", stamp.FeatureNames);
        WriteStringArray(writer, "FeatureColumns", stamp.FeatureColumns);
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
