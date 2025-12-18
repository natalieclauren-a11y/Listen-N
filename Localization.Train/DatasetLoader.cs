using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Localization.ML;

namespace Localization.Train;

internal static class DatasetLoader
{
    public static IReadOnlyList<LocalizationRow> LoadSingleSource(string path, double? fallbackDuration)
    {
        return LoadInternal(path, isDual: false, fallbackDuration);
    }

    public static IReadOnlyList<LocalizationRow> LoadDualSource(string path, double? fallbackDuration)
    {
        return LoadInternal(path, isDual: true, fallbackDuration);
    }

    private static IReadOnlyList<LocalizationRow> LoadInternal(string path, bool isDual, double? fallbackDuration)
    {
        var lines = File.ReadAllLines(path);
        if (lines.Length == 0)
        {
            return Array.Empty<LocalizationRow>();
        }

        var headers = lines[0].Split(',');
        var headerMap = headers
            .Select((h, idx) => (Header: h.Trim(), Index: idx))
            .ToDictionary(h => h.Header, h => h.Index, StringComparer.OrdinalIgnoreCase);

        var channelIndexes = Enumerable.Range(1, FeatureBuilder.ChannelCount)
            .Select(i => TryGetIndex(headerMap, $"Channel{i}") ?? -1)
            .ToArray();
        if (channelIndexes.Any(i => i < 0))
        {
            throw new InvalidOperationException($"File {path} is missing one or more Channel columns");
        }

        int? durationIndex = TryGetIndex(headerMap, "duration_s");
        double? inferredDuration = fallbackDuration ?? InferDurationFromName(path);

        int? xIndex = TryGetIndex(headerMap, "x");
        int? yIndex = TryGetIndex(headerMap, "y");
        int? zIndex = TryGetIndex(headerMap, "z");
        int? x1Index = TryGetIndex(headerMap, "x1");
        int? y1Index = TryGetIndex(headerMap, "y1");
        int? z1Index = TryGetIndex(headerMap, "z1");
        int? x2Index = TryGetIndex(headerMap, "x2");
        int? y2Index = TryGetIndex(headerMap, "y2");
        int? z2Index = TryGetIndex(headerMap, "z2");

        var rows = new List<LocalizationRow>();
        foreach (var line in lines.Skip(1))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var cols = line.Split(',');
            double[] channels = new double[FeatureBuilder.ChannelCount];
            for (int i = 0; i < FeatureBuilder.ChannelCount; i++)
            {
                channels[i] = ParseDouble(cols[channelIndexes[i]]);
            }

            double? duration = durationIndex.HasValue && durationIndex.Value < cols.Length
                ? ParseNullableDouble(cols[durationIndex.Value])
                : inferredDuration;

            if (isDual)
            {
                var dual = new double[6];
                dual[0] = ParseDouble(cols[x1Index!.Value]);
                dual[1] = ParseDouble(cols[y1Index!.Value]);
                dual[2] = ParseDouble(cols[z1Index!.Value]);
                dual[3] = ParseDouble(cols[x2Index!.Value]);
                dual[4] = ParseDouble(cols[y2Index!.Value]);
                dual[5] = ParseDouble(cols[z2Index!.Value]);
                rows.Add(new LocalizationRow
                {
                    Channels = channels,
                    DurationSeconds = duration,
                    IsDual = true,
                    DualCoordinates = dual
                });
            }
            else
            {
                var single = new double[3];
                single[0] = ParseDouble(cols[xIndex!.Value]);
                single[1] = ParseDouble(cols[yIndex!.Value]);
                single[2] = ParseDouble(cols[zIndex!.Value]);
                rows.Add(new LocalizationRow
                {
                    Channels = channels,
                    DurationSeconds = duration,
                    IsDual = false,
                    SingleCoordinates = single
                });
            }
        }

        return rows;
    }

    private static int? TryGetIndex(Dictionary<string, int> headerMap, string name)
    {
        return headerMap.TryGetValue(name, out var idx) ? idx : null;
    }

    private static double ParseDouble(string value)
    {
        return double.Parse(value, NumberStyles.Any, CultureInfo.InvariantCulture);
    }

    private static double? ParseNullableDouble(string value)
    {
        return double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var result) ? result : null;
    }

    private static double? InferDurationFromName(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var segments = name.Split('_');
        foreach (var segment in segments)
        {
            if (double.TryParse(segment, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed))
            {
                return parsed;
            }
        }

        return null;
    }
}
