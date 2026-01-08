using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Localization.Train;

internal static class TriggerPolicyWriter
{
    private const int SchemaVersion = 1;
    private const string MetricDefinition =
        "Median localization error over the tail set (TotalCounts >= threshold) within each duration bucket. " +
        "Threshold is the smallest TotalCounts bin edge where tail median error <= target and tail samples >= min_samples_per_bin.";

    internal static TriggerPolicyOutput ComputeAndWrite(
        string evalCsvPath,
        string outPath,
        double targetMedianMeters,
        int[] durationsSeconds,
        int binWidthCounts,
        int minSamplesPerBin,
        string? notes,
        string labelMode)
    {
        if (string.IsNullOrWhiteSpace(evalCsvPath))
        {
            throw new ArgumentException("Evaluation CSV path is required.");
        }

        if (string.IsNullOrWhiteSpace(outPath))
        {
            throw new ArgumentException("Output path is required.");
        }

        if (targetMedianMeters <= 0)
        {
            throw new ArgumentException("--trigger-policy-target-m must be positive.");
        }

        if (durationsSeconds is null || durationsSeconds.Length == 0)
        {
            throw new ArgumentException("--trigger-policy-durations must contain at least one duration.");
        }

        if (binWidthCounts <= 0)
        {
            throw new ArgumentException("--trigger-policy-bin-width must be positive.");
        }

        if (minSamplesPerBin <= 0)
        {
            throw new ArgumentException("--trigger-policy-min-samples-per-bin must be positive.");
        }

        var selection = ParseLabelMode(labelMode);

        var table = LoadCsvTable(evalCsvPath);
        if (table.Rows.Count == 0)
        {
            throw new InvalidOperationException("Evaluation CSV contains no data rows.");
        }

        var durationColumn = ResolveDurationColumn(table, out var durationColumnName);
        int? inferredDuration = null;
        if (!durationColumn.HasValue)
        {
            inferredDuration = InferDurationFromFilename(evalCsvPath, durationsSeconds);
        }

        var totalCountsColumn = ResolveTotalCountsColumn(table);
        var singleColumns = ResolveSingleColumns(table);
        var dualColumns = ResolveDualColumns(table);
        int? labelIndex = table.HeaderMap.TryGetValue("Label", out var labelIdx) ? labelIdx : null;
        int? isDualIndex = table.HeaderMap.TryGetValue("IsDual", out var isDualIdx) ? isDualIdx : null;

        var rowsByDuration = durationsSeconds.ToDictionary(
            d => d,
            _ => new Dictionary<LabelModeSelection, List<RowData>>
            {
                [LabelModeSelection.Single] = new List<RowData>(),
                [LabelModeSelection.Dual] = new List<RowData>()
            });

        var skippedMissingColumns = new Dictionary<LabelModeSelection, int>
        {
            [LabelModeSelection.Single] = 0,
            [LabelModeSelection.Dual] = 0
        };

        foreach (var row in table.Rows)
        {
            int durationSeconds = durationColumn.HasValue
                ? ParseDurationSeconds(SafeGet(row, durationColumn.Value), durationColumnName ?? "Duration_s")
                : inferredDuration!.Value;

            if (!rowsByDuration.ContainsKey(durationSeconds))
            {
                continue;
            }

            double totalCounts = ReadTotalCounts(table, row, totalCountsColumn);
            var rowMode = selection == LabelModeSelection.Auto
                ? ResolveRowMode(row, labelIndex, isDualIndex, dualColumns)
                : selection;

            if (rowMode == LabelModeSelection.Single)
            {
                if (selection == LabelModeSelection.Dual)
                {
                    continue;
                }

                var sc = singleColumns;
                if (sc is null)
                {
                    skippedMissingColumns[LabelModeSelection.Single]++;
                    continue;
                }

                if (!TryReadCoords(row, sc.True, out var truePos)
                    || !TryReadCoords(row, sc.Pred, out var predPos))
                {
                    skippedMissingColumns[LabelModeSelection.Single]++;
                    continue;
                }

                double errorMeters = ComputeSingleErrorMeters(truePos, predPos);
                rowsByDuration[durationSeconds][LabelModeSelection.Single].Add(new RowData(totalCounts, errorMeters));
            }
            else if (rowMode == LabelModeSelection.Dual)
            {
                if (selection == LabelModeSelection.Single)
                {
                    continue;
                }

                var dc = dualColumns;
                if (dc is null)
                {
                    skippedMissingColumns[LabelModeSelection.Dual]++;
                    continue;
                }

                if (!TryReadDualCoords(row, dc.True, out var truePair)
                    || !TryReadDualCoords(row, dc.Pred, out var predPair)
                    || truePair is null
                    || predPair is null)
                {
                    skippedMissingColumns[LabelModeSelection.Dual]++;
                    continue;
                }

                double errorMeters = ComputeDualErrorMeters(truePair, predPair);
                rowsByDuration[durationSeconds][LabelModeSelection.Dual].Add(new RowData(totalCounts, errorMeters));
            }
        }

        var thresholds = new Dictionary<string, TriggerPolicyThresholds>();
        var thresholdsMetadata = new Dictionary<string, TriggerPolicyThresholdMetadata>();
        var rowsUsed = new Dictionary<string, Dictionary<string, int>>();

        foreach (var duration in durationsSeconds)
        {
            var durationKey = duration.ToString(CultureInfo.InvariantCulture);
            var durationRows = rowsByDuration[duration];
            var durationUsage = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["single"] = durationRows[LabelModeSelection.Single].Count,
                ["dual"] = durationRows[LabelModeSelection.Dual].Count
            };
            rowsUsed[durationKey] = durationUsage;

            ThresholdComputationResult? singleResult = selection == LabelModeSelection.Dual
                ? null
                : ComputeThreshold(durationRows[LabelModeSelection.Single], targetMedianMeters, binWidthCounts, minSamplesPerBin);
            ThresholdComputationResult? dualResult = selection == LabelModeSelection.Single
                ? null
                : ComputeThreshold(durationRows[LabelModeSelection.Dual], targetMedianMeters, binWidthCounts, minSamplesPerBin);

            thresholds[durationKey] = new TriggerPolicyThresholds
            {
                Nmin15cmSingle = singleResult?.Threshold,
                Nmin15cmDual = dualResult?.Threshold
            };

            thresholdsMetadata[durationKey] = new TriggerPolicyThresholdMetadata
            {
                Single = singleResult is null ? null : BuildThresholdMetadata(singleResult),
                Dual = dualResult is null ? null : BuildThresholdMetadata(dualResult)
            };
        }

        var output = new TriggerPolicyOutput
        {
            SchemaVersion = SchemaVersion,
            CreatedUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            GitCommit = ResolveGitCommit(),
            Dataset = Path.GetFileName(evalCsvPath),
            MetricDefinition = MetricDefinition,
            Binning = new TriggerPolicyBinning
            {
                BinWidthCounts = binWidthCounts,
                MinSamplesPerBin = minSamplesPerBin
            },
            Target = new TriggerPolicyTarget
            {
                TargetMedianErrorMeters = targetMedianMeters,
                DurationsSeconds = durationsSeconds
            },
            Thresholds = thresholds,
            ColumnMappingUsed = BuildColumnMapping(totalCountsColumn, durationColumnName, labelIndex, isDualIndex, singleColumns, dualColumns),
            RowsTotal = table.Rows.Count,
            RowsUsedPerDurationAndMode = rowsUsed,
            RowsSkippedMissingColumns = new TriggerPolicySkippedRows
            {
                Single = skippedMissingColumns[LabelModeSelection.Single],
                Dual = skippedMissingColumns[LabelModeSelection.Dual]
            },
            SelectionRule = MetricDefinition,
            Notes = notes,
            ThresholdsMetadata = thresholdsMetadata,
            LabelMode = selection.ToString().ToLowerInvariant()
        };

        var json = JsonSerializer.Serialize(output, new JsonSerializerOptions { WriteIndented = true });
        Directory.CreateDirectory(Path.GetDirectoryName(outPath) ?? ".");
        File.WriteAllText(outPath, json);

        return output;
    }

    private static TriggerPolicyThresholdDetail BuildThresholdMetadata(ThresholdComputationResult result)
    {
        return new TriggerPolicyThresholdDetail
        {
            UnmetTarget = result.UnmetTarget,
            TailMedianErrorMeters = result.TailMedianErrorMeters,
            TailSamples = result.TailSamples,
            MaxTotalCountsObserved = result.MaxTotalCountsObserved
        };
    }

    private static TriggerPolicyColumnMapping BuildColumnMapping(
        TotalCountsColumn totalCountsColumn,
        string? durationColumnName,
        int? labelIndex,
        int? isDualIndex,
        SingleCoordColumns? singleColumns,
        DualCoordColumns? dualColumns)
    {
        return new TriggerPolicyColumnMapping
        {
            TotalCountsColumn = totalCountsColumn.TotalCountsIndex.HasValue ? totalCountsColumn.TotalCountsName : null,
            ChannelColumns = totalCountsColumn.ChannelNames,
            DurationColumn = durationColumnName,
            LabelColumn = labelIndex.HasValue ? "Label" : null,
            IsDualColumn = isDualIndex.HasValue ? "IsDual" : null,
            SingleTrueColumns = singleColumns?.True.Names,
            SinglePredColumns = singleColumns?.Pred.Names,
            DualTrue1Columns = dualColumns?.True.First.Names,
            DualTrue2Columns = dualColumns?.True.Second.Names,
            DualPred1Columns = dualColumns?.Pred.First.Names,
            DualPred2Columns = dualColumns?.Pred.Second.Names
        };
    }

    private static ThresholdComputationResult ComputeThreshold(List<RowData> rows, double targetMedianMeters, int binWidthCounts, int minSamplesPerBin)
    {
        if (rows.Count == 0)
        {
            return new ThresholdComputationResult(null, true, null, 0, 0);
        }

        double minCounts = rows.Min(r => r.TotalCounts);
        double maxCounts = rows.Max(r => r.TotalCounts);
        int minBin = (int)Math.Floor(minCounts / binWidthCounts);
        int maxBin = (int)Math.Floor(maxCounts / binWidthCounts);

        int? selectedThreshold = null;
        double? selectedMedian = null;
        int selectedSamples = 0;

        for (int bin = minBin; bin <= maxBin; bin++)
        {
            int threshold = bin * binWidthCounts;
            var tailRows = rows.Where(r => r.TotalCounts >= threshold).ToList();
            if (tailRows.Count < minSamplesPerBin)
            {
                continue;
            }

            double median = Median(tailRows.Select(r => r.ErrorMeters).ToList());
            if (median <= targetMedianMeters)
            {
                selectedThreshold = threshold;
                selectedMedian = median;
                selectedSamples = tailRows.Count;
                break;
            }
        }

        if (!selectedThreshold.HasValue)
        {
            int thresholdAtMax = (int)Math.Round(maxCounts, MidpointRounding.AwayFromZero);
            double medianAtMax = Median(rows.Where(r => r.TotalCounts >= thresholdAtMax).Select(r => r.ErrorMeters).ToList());
            return new ThresholdComputationResult(thresholdAtMax, true, medianAtMax, rows.Count, maxCounts);
        }

        return new ThresholdComputationResult(selectedThreshold.Value, false, selectedMedian, selectedSamples, maxCounts);
    }

    private static CsvTable LoadCsvTable(string path)
    {
        var lines = File.ReadAllLines(path);
        if (lines.Length == 0)
        {
            return new CsvTable(Array.Empty<string>(), new List<string[]>());
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

        return new CsvTable(headers, rows);
    }

    private static int ParseDurationSeconds(string value, string columnName)
    {
        double parsed = ParseDouble(value, columnName);
        return (int)Math.Round(parsed, MidpointRounding.AwayFromZero);
    }

    private static int InferDurationFromFilename(string path, int[] durationsSeconds)
    {
        string name = Path.GetFileNameWithoutExtension(path);
        var matches = new List<int>();
        foreach (var duration in durationsSeconds)
        {
            string pattern = $@"(?<!\d){duration}(?!\d)";
            if (System.Text.RegularExpressions.Regex.IsMatch(name, pattern))
            {
                matches.Add(duration);
            }
        }

        if (matches.Count == 1)
        {
            return matches[0];
        }

        if (matches.Count == 0)
        {
            throw new InvalidOperationException("Duration_s column not found and duration could not be inferred from the filename.");
        }

        throw new InvalidOperationException("Duration_s column not found and multiple duration tokens were found in the filename.");
    }

    private static int? ResolveDurationColumn(CsvTable table, out string? columnName)
    {
        string[] candidates = { "Duration_s", "duration_s", "DurationSeconds", "Duration" };
        foreach (var candidate in candidates)
        {
            if (table.HeaderMap.TryGetValue(candidate, out var idx))
            {
                columnName = candidate;
                return idx;
            }
        }

        columnName = null;
        return null;
    }

    private static TotalCountsColumn ResolveTotalCountsColumn(CsvTable table)
    {
        if (table.HeaderMap.TryGetValue("TotalCounts", out var totalIndex))
        {
            return new TotalCountsColumn(totalIndex, "TotalCounts", Array.Empty<int>(), Array.Empty<string>());
        }

        var channelIndices = new List<int>();
        var channelNames = new List<string>();
        for (int i = 1; i <= 15; i++)
        {
            string preferred = $"Channel{i}";
            string fallback = $"c{i}";
            if (table.HeaderMap.TryGetValue(preferred, out var idx))
            {
                channelIndices.Add(idx);
                channelNames.Add(preferred);
            }
            else if (table.HeaderMap.TryGetValue(fallback, out idx))
            {
                channelIndices.Add(idx);
                channelNames.Add(fallback);
            }
        }

        if (channelIndices.Count != 15)
        {
            throw new InvalidOperationException("Evaluation CSV must contain TotalCounts or a full set of Channel1..Channel15 (or c1..c15) columns.");
        }

        return new TotalCountsColumn(null, null, channelIndices.ToArray(), channelNames.ToArray());
    }

    private static SingleCoordColumns? ResolveSingleColumns(CsvTable table)
    {
        var options = new (string[] True, string[] Pred)[]
        {
            (new[] { "true_x", "true_y", "true_z" }, new[] { "pred_x", "pred_y", "pred_z" }),
            (new[] { "x", "y", "z" }, new[] { "pred_x", "pred_y", "pred_z" }),
            (new[] { "x", "y", "z" }, new[] { "x_hat", "y_hat", "z_hat" })
        };

        foreach (var option in options)
        {
            if (TryResolveCoords(table.HeaderMap, option.True, out var trueCoords)
                && TryResolveCoords(table.HeaderMap, option.Pred, out var predCoords))
            {
                return new SingleCoordColumns(trueCoords, predCoords);
            }
        }

        return null;
    }

    private static DualCoordColumns? ResolveDualColumns(CsvTable table)
    {
        var options = new (string[] True1, string[] True2, string[] Pred1, string[] Pred2)[]
        {
            (
                new[] { "true_x1", "true_y1", "true_z1" },
                new[] { "true_x2", "true_y2", "true_z2" },
                new[] { "pred_x1", "pred_y1", "pred_z1" },
                new[] { "pred_x2", "pred_y2", "pred_z2" }
            ),
            (
                new[] { "x1", "y1", "z1" },
                new[] { "x2", "y2", "z2" },
                new[] { "pred_x1", "pred_y1", "pred_z1" },
                new[] { "pred_x2", "pred_y2", "pred_z2" }
            ),
            (
                new[] { "x1", "y1", "z1" },
                new[] { "x2", "y2", "z2" },
                new[] { "x1_hat", "y1_hat", "z1_hat" },
                new[] { "x2_hat", "y2_hat", "z2_hat" }
            )
        };

        foreach (var option in options)
        {
            if (TryResolveCoords(table.HeaderMap, option.True1, out var true1)
                && TryResolveCoords(table.HeaderMap, option.True2, out var true2)
                && TryResolveCoords(table.HeaderMap, option.Pred1, out var pred1)
                && TryResolveCoords(table.HeaderMap, option.Pred2, out var pred2))
            {
                return new DualCoordColumns(new DualCoordSet(true1, true2), new DualCoordSet(pred1, pred2));
            }
        }

        return null;
    }

    private static bool TryResolveCoords(Dictionary<string, int> headerMap, string[] names, out CoordColumns coords)
    {
        coords = null!;
        if (!headerMap.TryGetValue(names[0], out var x)
            || !headerMap.TryGetValue(names[1], out var y)
            || !headerMap.TryGetValue(names[2], out var z))
        {
            return false;
        }

        coords = new CoordColumns(x, y, z, names);
        return true;
    }

    private static LabelModeSelection ResolveRowMode(string[] row, int? labelIndex, int? isDualIndex, DualCoordColumns? dualColumns)
    {
        if (labelIndex.HasValue)
        {
            string value = SafeGet(row, labelIndex.Value).Trim();
            if (value.Contains("dual", StringComparison.OrdinalIgnoreCase))
            {
                return LabelModeSelection.Dual;
            }

            if (value.Contains("single", StringComparison.OrdinalIgnoreCase))
            {
                return LabelModeSelection.Single;
            }

            throw new InvalidOperationException($"Unrecognized Label value '{value}'.");
        }

        if (isDualIndex.HasValue)
        {
            string value = SafeGet(row, isDualIndex.Value).Trim();
            if (!bool.TryParse(value, out var isDual))
            {
                throw new InvalidOperationException($"IsDual value '{value}' was not recognized as a boolean.");
            }

            return isDual ? LabelModeSelection.Dual : LabelModeSelection.Single;
        }

        if (dualColumns is not null
            && TryReadDualCoords(row, dualColumns.True, out _)
            && TryReadDualCoords(row, dualColumns.Pred, out _))
        {
            return LabelModeSelection.Dual;
        }

        return LabelModeSelection.Single;
    }

    private static LabelModeSelection ParseLabelMode(string labelMode)
    {
        if (string.Equals(labelMode, "single", StringComparison.OrdinalIgnoreCase))
        {
            return LabelModeSelection.Single;
        }

        if (string.Equals(labelMode, "dual", StringComparison.OrdinalIgnoreCase))
        {
            return LabelModeSelection.Dual;
        }

        if (string.Equals(labelMode, "auto", StringComparison.OrdinalIgnoreCase))
        {
            return LabelModeSelection.Auto;
        }

        throw new ArgumentException("--trigger-policy-label-mode must be one of 'single', 'dual', or 'auto'.");
    }

    private static double ReadTotalCounts(CsvTable table, string[] row, TotalCountsColumn totalCountsColumn)
    {
        if (totalCountsColumn.TotalCountsIndex.HasValue)
        {
            string value = SafeGet(row, totalCountsColumn.TotalCountsIndex.Value).Trim();
            return ParseDouble(value, totalCountsColumn.TotalCountsName ?? "TotalCounts");
        }

        double sum = 0;
        foreach (var idx in totalCountsColumn.ChannelIndices)
        {
            string value = SafeGet(row, idx).Trim();
            sum += ParseDouble(value, "ChannelCounts");
        }

        return sum;
    }

    private static bool TryReadCoords(string[] row, CoordColumns coords, out (double X, double Y, double Z) value)
    {
        value = default;
        if (!double.TryParse(SafeGet(row, coords.X).Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out var x)
            || !double.TryParse(SafeGet(row, coords.Y).Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out var y)
            || !double.TryParse(SafeGet(row, coords.Z).Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out var z))
        {
            return false;
        }

        if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(z))
        {
            return false;
        }

        value = (x, y, z);
        return true;
    }

    private static bool TryReadDualCoords(string[] row, DualCoordSet coords, out DualCoords value)
    {
        value = default;
        if (!TryReadCoords(row, coords.First, out var first) || !TryReadCoords(row, coords.Second, out var second))
        {
            return false;
        }

        value = new DualCoords(first, second);
        return true;
    }

    private static double ComputeSingleErrorMeters((double X, double Y, double Z) truePos, (double X, double Y, double Z) predPos)
    {
        double dx = predPos.X - truePos.X;
        double dy = predPos.Y - truePos.Y;
        double dz = predPos.Z - truePos.Z;
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    private static double ComputeDualErrorMeters(DualCoords trueCoords, DualCoords predCoords)
    {
        double option1 = MaxDistanceMeters(trueCoords.First, predCoords.First, trueCoords.Second, predCoords.Second);
        double option2 = MaxDistanceMeters(trueCoords.First, predCoords.Second, trueCoords.Second, predCoords.First);
        return Math.Min(option1, option2);
    }

    private static double MaxDistanceMeters((double X, double Y, double Z) true1, (double X, double Y, double Z) pred1, (double X, double Y, double Z) true2, (double X, double Y, double Z) pred2)
    {
        double d1 = ComputeSingleErrorMeters(true1, pred1);
        double d2 = ComputeSingleErrorMeters(true2, pred2);
        return Math.Max(d1, d2);
    }

    private static double Median(List<double> values)
    {
        values.Sort();
        int count = values.Count;
        if (count == 0)
        {
            return double.NaN;
        }

        int mid = count / 2;
        if (count % 2 == 1)
        {
            return values[mid];
        }

        return (values[mid - 1] + values[mid]) / 2.0;
    }

    private static double ParseDouble(string value, string label)
    {
        if (!double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var result))
        {
            throw new InvalidOperationException($"Unable to parse {label} value '{value}'.");
        }

        if (!double.IsFinite(result))
        {
            throw new InvalidOperationException($"Parsed {label} value '{value}' was not finite.");
        }

        return result;
    }

    private static string SafeGet(string[] row, int index)
    {
        return index >= 0 && index < row.Length ? row[index] : string.Empty;
    }

    private static string? ResolveGitCommit()
    {
        string? envCommit = Environment.GetEnvironmentVariable("GIT_COMMIT");
        if (!string.IsNullOrWhiteSpace(envCommit))
        {
            return envCommit;
        }

        try
        {
            var startInfo = new ProcessStartInfo("git", "rev-parse HEAD")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return null;
            }

            string output = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit(2000);
            if (process.ExitCode == 0 && output.Length > 0)
            {
                return output;
            }
        }
        catch (Exception)
        {
            return null;
        }

        return null;
    }

    private sealed record CsvTable(string[] Headers, List<string[]> Rows)
    {
        public Dictionary<string, int> HeaderMap { get; } = Headers
            .Select((h, idx) => (Header: h.Trim(), Index: idx))
            .ToDictionary(h => h.Header, h => h.Index, StringComparer.OrdinalIgnoreCase);
    }

    private sealed record TotalCountsColumn(int? TotalCountsIndex, string? TotalCountsName, int[] ChannelIndices, string[] ChannelNames);

    private sealed record CoordColumns(int X, int Y, int Z, string[] Names);

    private sealed record SingleCoordColumns(CoordColumns True, CoordColumns Pred);

    private sealed record DualCoordSet(CoordColumns First, CoordColumns Second);

    private sealed record DualCoordColumns(DualCoordSet True, DualCoordSet Pred);

    private sealed record DualCoords((double X, double Y, double Z) First, (double X, double Y, double Z) Second);

    private sealed record RowData(double TotalCounts, double ErrorMeters);

    private sealed record ThresholdComputationResult(int? Threshold, bool UnmetTarget, double? TailMedianErrorMeters, int TailSamples, double MaxTotalCountsObserved);

    private enum LabelModeSelection
    {
        Single,
        Dual,
        Auto
    }
}

internal sealed record TriggerPolicyOutput
{
    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; init; }

    [JsonPropertyName("created_utc")]
    public string CreatedUtc { get; init; } = string.Empty;

    [JsonPropertyName("git_commit")]
    public string? GitCommit { get; init; }

    [JsonPropertyName("dataset")]
    public string Dataset { get; init; } = string.Empty;

    [JsonPropertyName("metric_definition")]
    public string MetricDefinition { get; init; } = string.Empty;

    [JsonPropertyName("binning")]
    public TriggerPolicyBinning Binning { get; init; } = new();

    [JsonPropertyName("target")]
    public TriggerPolicyTarget Target { get; init; } = new();

    [JsonPropertyName("thresholds")]
    public Dictionary<string, TriggerPolicyThresholds> Thresholds { get; init; } = new();

    [JsonPropertyName("column_mapping_used")]
    public TriggerPolicyColumnMapping ColumnMappingUsed { get; init; } = new();

    [JsonPropertyName("rows_total")]
    public int RowsTotal { get; init; }

    [JsonPropertyName("rows_used_per_duration_and_mode")]
    public Dictionary<string, Dictionary<string, int>> RowsUsedPerDurationAndMode { get; init; } = new();

    [JsonPropertyName("rows_skipped_missing_columns")]
    public TriggerPolicySkippedRows RowsSkippedMissingColumns { get; init; } = new();

    [JsonPropertyName("selection_rule")]
    public string SelectionRule { get; init; } = string.Empty;

    [JsonPropertyName("notes")]
    public string? Notes { get; init; }

    [JsonPropertyName("thresholds_metadata")]
    public Dictionary<string, TriggerPolicyThresholdMetadata> ThresholdsMetadata { get; init; } = new();

    [JsonPropertyName("label_mode")]
    public string LabelMode { get; init; } = "auto";
}

internal sealed record TriggerPolicyBinning
{
    [JsonPropertyName("bin_width_counts")]
    public int BinWidthCounts { get; init; }

    [JsonPropertyName("min_samples_per_bin")]
    public int MinSamplesPerBin { get; init; }
}

internal sealed record TriggerPolicyTarget
{
    [JsonPropertyName("target_median_error_m")]
    public double TargetMedianErrorMeters { get; init; }

    [JsonPropertyName("durations_s")]
    public int[] DurationsSeconds { get; init; } = Array.Empty<int>();
}

internal sealed record TriggerPolicyThresholds
{
    [JsonPropertyName("nmin_15cm_single")]
    public int? Nmin15cmSingle { get; init; }

    [JsonPropertyName("nmin_15cm_dual")]
    public int? Nmin15cmDual { get; init; }
}

internal sealed record TriggerPolicyThresholdMetadata
{
    [JsonPropertyName("single")]
    public TriggerPolicyThresholdDetail? Single { get; init; }

    [JsonPropertyName("dual")]
    public TriggerPolicyThresholdDetail? Dual { get; init; }
}

internal sealed record TriggerPolicyThresholdDetail
{
    [JsonPropertyName("unmet_target")]
    public bool UnmetTarget { get; init; }

    [JsonPropertyName("tail_median_error_m")]
    public double? TailMedianErrorMeters { get; init; }

    [JsonPropertyName("tail_samples")]
    public int TailSamples { get; init; }

    [JsonPropertyName("max_total_counts_observed")]
    public double MaxTotalCountsObserved { get; init; }
}

internal sealed record TriggerPolicyColumnMapping
{
    [JsonPropertyName("total_counts_column")]
    public string? TotalCountsColumn { get; init; }

    [JsonPropertyName("channel_columns")]
    public string[]? ChannelColumns { get; init; }

    [JsonPropertyName("duration_column")]
    public string? DurationColumn { get; init; }

    [JsonPropertyName("label_column")]
    public string? LabelColumn { get; init; }

    [JsonPropertyName("is_dual_column")]
    public string? IsDualColumn { get; init; }

    [JsonPropertyName("single_true_columns")]
    public string[]? SingleTrueColumns { get; init; }

    [JsonPropertyName("single_pred_columns")]
    public string[]? SinglePredColumns { get; init; }

    [JsonPropertyName("dual_true_1_columns")]
    public string[]? DualTrue1Columns { get; init; }

    [JsonPropertyName("dual_true_2_columns")]
    public string[]? DualTrue2Columns { get; init; }

    [JsonPropertyName("dual_pred_1_columns")]
    public string[]? DualPred1Columns { get; init; }

    [JsonPropertyName("dual_pred_2_columns")]
    public string[]? DualPred2Columns { get; init; }
}

internal sealed record TriggerPolicySkippedRows
{
    [JsonPropertyName("single")]
    public int Single { get; init; }

    [JsonPropertyName("dual")]
    public int Dual { get; init; }
}
