using System;
using System.Collections.Generic;
using System.Globalization;
using System.Data;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using ExcelDataReader;
using Localization.ML;

namespace Localization.Train;

internal static class DatasetLoader
{
    private static readonly string[] PreferredJoinKeys = { "pair_id", "pairid", "pair", "id", "index", "row" };

    public static IReadOnlyList<LocalizationRow> LoadSingleSource(string path, double? fallbackDuration)
    {
        return LoadSingleInternal(path, fallbackDuration);
    }

    public static IReadOnlyList<LocalizationRow> LoadDualSource(string path, double? fallbackDuration)
    {
        return LoadDualSource(path, pairMetadataPath: null, fallbackDuration);
    }

    public static IReadOnlyList<LocalizationRow> LoadDualSource(string path, string? pairMetadataPath, double? fallbackDuration)
    {
        var countsTable = LoadTable(path);
        if (countsTable.Rows.Count == 0)
        {
            return Array.Empty<LocalizationRow>();
        }

        var channelIndexes = ResolveChannelIndexes(countsTable, path);
        int? durationIndex = TryGetIndex(countsTable.HeaderMap, "duration_s");
        double? inferredDuration = fallbackDuration ?? InferDurationFromName(path);

        var coordinateTable = pairMetadataPath == null
            ? countsTable
            : LoadTable(pairMetadataPath);

        var coordIndexes = ResolveDualCoordinateIndexes(coordinateTable, pairMetadataPath ?? path);

        string? joinKey = FindJoinKey(countsTable.Headers, coordinateTable.Headers);
        var rows = new List<LocalizationRow>();
        if (joinKey != null)
        {
            var metadataLookup = BuildMetadataLookup(coordinateTable, coordIndexes, joinKey);
            for (int rowIndex = 0; rowIndex < countsTable.Rows.Count; rowIndex++)
            {
                var countRow = countsTable.Rows[rowIndex];
                if (!TryGetValue(countsTable, countRow, joinKey, out var keyValue))
                {
                    continue;
                }

                if (!metadataLookup.TryGetValue(keyValue, out var metadataEntry))
                {
                    continue;
                }

                var metadata = ExtractMetadata(countsTable, countRow, rowIndex, joinKey, coordinateRowIndex: metadataEntry.RowIndex);
                rows.Add(CreateDualRow(countRow, channelIndexes, durationIndex, inferredDuration, metadataEntry.Coords, metadata));
            }
        }
        else
        {
            int rowCount = Math.Min(countsTable.Rows.Count, coordinateTable.Rows.Count);
            for (int i = 0; i < rowCount; i++)
            {
                var dualCoords = ParseDualCoords(coordinateTable.Rows[i], coordIndexes);
                var metadata = ExtractMetadata(countsTable, countsTable.Rows[i], i, joinKey: null, coordinateRowIndex: i);
                rows.Add(CreateDualRow(countsTable.Rows[i], channelIndexes, durationIndex, inferredDuration, dualCoords, metadata));
            }
        }

        return rows;
    }

    private static IReadOnlyList<LocalizationRow> LoadSingleInternal(string path, double? fallbackDuration)
    {
        var table = LoadTable(path);
        if (table.Rows.Count == 0)
        {
            return Array.Empty<LocalizationRow>();
        }

        var channelIndexes = ResolveChannelIndexes(table, path);
        int? durationIndex = TryGetIndex(table.HeaderMap, "duration_s");
        double? inferredDuration = fallbackDuration ?? InferDurationFromName(path);

        int? xIndex = TryGetIndex(table.HeaderMap, "x");
        int? yIndex = TryGetIndex(table.HeaderMap, "y");
        int? zIndex = TryGetIndex(table.HeaderMap, "z");
        int? fileNameIndex = TryGetIndex(table.HeaderMap, "File Name")
            ?? TryGetIndex(table.HeaderMap, "FileName")
            ?? TryGetIndex(table.HeaderMap, "filename");

        bool hasExplicitCoords = xIndex.HasValue && yIndex.HasValue && zIndex.HasValue;
        if (!hasExplicitCoords && !fileNameIndex.HasValue)
        {
            var headerPreview = table.Headers
                .Select(h => (h ?? string.Empty).Trim())
                .Take(30)
                .ToArray();
            var headerSummary = headerPreview.Length == 0
                ? "(none)"
                : string.Join(", ", headerPreview);
            throw new InvalidOperationException(
                $"File {path} is missing single coordinate columns. Expected x,y,z or File Name. " +
                $"Headers found (first {headerPreview.Length}): {headerSummary}");
        }

        var rows = new List<LocalizationRow>();
        for (int rowIndex = 0; rowIndex < table.Rows.Count; rowIndex++)
        {
            var cols = table.Rows[rowIndex];
            double[] channels = new double[FeatureBuilder.ChannelCount];
            for (int i = 0; i < FeatureBuilder.ChannelCount; i++)
            {
                channels[i] = ParseDouble(SafeGet(cols, channelIndexes[i]));
            }

            double? duration = durationIndex.HasValue
                ? ParseNullableDouble(SafeGet(cols, durationIndex.Value))
                : inferredDuration;

            var single = new double[3];
            if (hasExplicitCoords)
            {
                single[0] = ParseDouble(SafeGet(cols, xIndex!.Value));
                single[1] = ParseDouble(SafeGet(cols, yIndex!.Value));
                single[2] = ParseDouble(SafeGet(cols, zIndex!.Value));
            }
            else
            {
                var rawFileName = SafeGet(cols, fileNameIndex!.Value);
                if (!TryParseSingleCoordsFromFileName(rawFileName, out single[0], out single[1], out single[2]))
                {
                    Console.WriteLine($"Warning: Unable to parse coordinates from file name '{rawFileName}'. Skipping row.");
                    continue;
                }
            }

            var metadata = ExtractMetadata(table, cols, rowIndex, joinKey: null, coordinateRowIndex: rowIndex);
            rows.Add(new LocalizationRow
            {
                Channels = channels,
                DurationSeconds = duration,
                IsDual = false,
                SingleCoordinates = single,
                Metadata = metadata
            });
        }

        return rows;
    }

    private static LocalizationRow CreateDualRow(string[] cols, int[] channelIndexes, int? durationIndex, double? inferredDuration, double[] dualCoords, IReadOnlyDictionary<string, string>? metadata)
    {
        double[] channels = new double[FeatureBuilder.ChannelCount];
        for (int i = 0; i < FeatureBuilder.ChannelCount; i++)
        {
            channels[i] = ParseDouble(SafeGet(cols, channelIndexes[i]));
        }

        double? duration = durationIndex.HasValue
            ? ParseNullableDouble(SafeGet(cols, durationIndex.Value))
            : inferredDuration;

        return new LocalizationRow
        {
            Channels = channels,
            DurationSeconds = duration,
            IsDual = true,
            DualCoordinates = dualCoords,
            Metadata = metadata
        };
    }

    private static int[] ResolveChannelIndexes(Table table, string path)
    {
        var missingChannels = new List<string>();
        var channelIndexes = new int[FeatureBuilder.ChannelCount];
        for (int i = 1; i <= FeatureBuilder.ChannelCount; i++)
        {
            var exactHeader = $"Channel{i}";
            var spacedHeader = $"Channel {i}";
            int? index = TryGetIndex(table.HeaderMap, exactHeader)
                ?? TryGetIndex(table.HeaderMap, spacedHeader);
            if (!index.HasValue)
            {
                missingChannels.Add(exactHeader);
                channelIndexes[i - 1] = -1;
                continue;
            }

            channelIndexes[i - 1] = index.Value;
        }

        if (missingChannels.Count > 0)
        {
            var headerPreview = table.Headers
                .Select(h => (h ?? string.Empty).Trim())
                .Take(30)
                .ToArray();
            var headerSummary = headerPreview.Length == 0
                ? "(none)"
                : string.Join(", ", headerPreview);
            throw new InvalidOperationException(
                $"File {path} is missing channel columns: {string.Join(", ", missingChannels)}. " +
                $"Headers found (first {headerPreview.Length}): {headerSummary}");
        }

        return channelIndexes;
    }

    private static int[] ResolveDualCoordinateIndexes(Table table, string path)
    {
        int? x1Index = TryGetIndex(table.HeaderMap, "x1");
        int? y1Index = TryGetIndex(table.HeaderMap, "y1");
        int? z1Index = TryGetIndex(table.HeaderMap, "z1");
        int? x2Index = TryGetIndex(table.HeaderMap, "x2");
        int? y2Index = TryGetIndex(table.HeaderMap, "y2");
        int? z2Index = TryGetIndex(table.HeaderMap, "z2");

        if (!x1Index.HasValue || !y1Index.HasValue || !z1Index.HasValue || !x2Index.HasValue || !y2Index.HasValue || !z2Index.HasValue)
        {
            throw new InvalidOperationException($"File {path} is missing one or more dual coordinate columns (x1,y1,z1,x2,y2,z2)");
        }

        return new[] { x1Index.Value, y1Index.Value, z1Index.Value, x2Index.Value, y2Index.Value, z2Index.Value };
    }

    private static double[] ParseDualCoords(string[] cols, int[] coordIndexes)
    {
        var dual = new double[6];
        for (int i = 0; i < coordIndexes.Length; i++)
        {
            dual[i] = ParseDouble(SafeGet(cols, coordIndexes[i]));
        }

        return dual;
    }

    private static Dictionary<string, (double[] Coords, int RowIndex)> BuildMetadataLookup(Table table, int[] coordIndexes, string joinKey)
    {
        var lookup = new Dictionary<string, (double[] Coords, int RowIndex)>(StringComparer.OrdinalIgnoreCase);
        for (int rowIndex = 0; rowIndex < table.Rows.Count; rowIndex++)
        {
            var row = table.Rows[rowIndex];
            if (!TryGetValue(table, row, joinKey, out var key))
            {
                continue;
            }

            lookup[key] = (ParseDualCoords(row, coordIndexes), rowIndex);
        }

        return lookup;
    }

    private static IReadOnlyDictionary<string, string>? ExtractMetadata(Table table, string[] row, int rowIndex, string? joinKey, int? coordinateRowIndex)
    {
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (joinKey != null && TryGetValue(table, row, joinKey, out var joinValue))
        {
            metadata[joinKey] = joinValue;
        }

        foreach (var candidate in new[] { "run", "run_id", "runid", "runidnum", "position", "position_index", "positionindex", "pair_id", "pairid", "pair", "id", "index", "row" })
        {
            if (metadata.ContainsKey(candidate))
            {
                continue;
            }

            if (TryGetValue(table, row, candidate, out var value))
            {
                metadata[candidate] = value;
            }
        }

        metadata["row_index"] = rowIndex.ToString(CultureInfo.InvariantCulture);
        if (coordinateRowIndex.HasValue)
        {
            metadata["coordinate_row_index"] = coordinateRowIndex.Value.ToString(CultureInfo.InvariantCulture);
        }

        return metadata.Count == 0 ? null : metadata;
    }

    private static string? FindJoinKey(string[] countHeaders, string[] metadataHeaders)
    {
        var metadataSet = new HashSet<string>(metadataHeaders, StringComparer.OrdinalIgnoreCase);
        foreach (var key in PreferredJoinKeys)
        {
            if (countHeaders.Any(h => string.Equals(h, key, StringComparison.OrdinalIgnoreCase)) && metadataSet.Contains(key))
            {
                return key;
            }
        }

        return null;
    }

    private static bool TryGetValue(Table table, string[] row, string header, out string value)
    {
        value = string.Empty;
        if (!table.HeaderMap.TryGetValue(header, out var idx))
        {
            return false;
        }

        value = SafeGet(row, idx).Trim();
        return value.Length > 0;
    }

    private static string SafeGet(string[] cols, int index)
    {
        return index >= 0 && index < cols.Length ? cols[index] : string.Empty;
    }

    private static Table LoadTable(string path)
    {
        var extension = Path.GetExtension(path);
        if (string.Equals(extension, ".xlsx", StringComparison.OrdinalIgnoreCase))
        {
            return LoadExcelTable(path);
        }

        return LoadCsvTable(path);
    }

    private static Table LoadCsvTable(string path)
    {
        var lines = File.ReadAllLines(path);
        if (lines.Length == 0)
        {
            return new Table(Array.Empty<string>(), new List<string[]>());
        }

        var headers = lines[0].Split(',').Select(h => h.Trim()).ToArray();
        var rows = new List<string[]>();
        foreach (var line in lines.Skip(1))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            rows.Add(line.Split(','));
        }

        return new Table(headers, rows);
    }

    private static Table LoadExcelTable(string path)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = ExcelReaderFactory.CreateReader(stream);
        using var dataSet = reader.AsDataSet();

        if (dataSet.Tables.Count == 0)
        {
            return new Table(Array.Empty<string>(), new List<string[]>());
        }

        DataTable table = dataSet.Tables[0];
        if (table.Rows.Count == 0)
        {
            return new Table(Array.Empty<string>(), new List<string[]>());
        }

        var headers = table.Rows[0].ItemArray.Select(cell => (cell?.ToString() ?? string.Empty).Trim()).ToArray();
        var rows = new List<string[]>();
        for (int i = 1; i < table.Rows.Count; i++)
        {
            var row = table.Rows[i].ItemArray.Select(cell => cell?.ToString() ?? string.Empty).ToArray();
            rows.Add(row);
        }

        return new Table(headers, rows);
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

    private static bool TryParseSingleCoordsFromFileName(string fileName, out double x, out double y, out double z)
    {
        x = 0;
        y = 0;
        z = 0;
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return false;
        }

        var baseName = Path.GetFileName(fileName.Trim());
        var matches = Regex.Matches(baseName, @"[-+]?\d+(?:\.\d+)?", RegexOptions.CultureInvariant);
        if (matches.Count < 4)
        {
            return false;
        }

        if (!int.TryParse(matches[0].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
        {
            return false;
        }

        return double.TryParse(matches[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out x)
            && double.TryParse(matches[2].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out y)
            && double.TryParse(matches[3].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out z);
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

    private sealed record Table(string[] Headers, List<string[]> Rows)
    {
        public Dictionary<string, int> HeaderMap { get; } = Headers
            .Select((h, idx) => (Header: h.Trim(), Index: idx))
            .ToDictionary(h => h.Header, h => h.Index, StringComparer.OrdinalIgnoreCase);
    }
}
