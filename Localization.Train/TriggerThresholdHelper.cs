using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Localization.Train;

internal static class TriggerThresholdHelper
{
    private const string NotesText = "Computed from evaluation CSV by quantile-binning TotalCounts and selecting the smallest threshold whose tail median error meets target.";

    internal static TriggerThresholdResult ComputeAndWrite(string evalCsvPath, string triggerJsonOutPath, double errorTargetCm, int bins, int minSamplesPerBin)
    {
        if (string.IsNullOrWhiteSpace(evalCsvPath))
        {
            throw new ArgumentException("--eval-csv must be provided when computing trigger thresholds.");
        }

        if (string.IsNullOrWhiteSpace(triggerJsonOutPath))
        {
            throw new ArgumentException("--trigger-json-out must be provided when computing trigger thresholds.");
        }

        var table = LoadCsvTable(evalCsvPath);
        if (table.Rows.Count == 0)
        {
            throw new InvalidOperationException("Evaluation CSV contains no data rows.");
        }

        var totalCountsColumn = ResolveTotalCountsColumn(table);
        var singleColumns = ResolveSingleColumns(table);
        var dualColumns = ResolveDualColumns(table);
        var hasLabelColumn = table.HeaderMap.TryGetValue("Label", out var labelIndex);
        var hasIsDualColumn = table.HeaderMap.TryGetValue("IsDual", out var isDualIndex);

        var rows = new List<RowData>(table.Rows.Count);
        foreach (var row in table.Rows)
        {
            double totalCounts = ReadTotalCounts(table, row, totalCountsColumn);
            var label = ResolveLabel(row, labelIndex, isDualIndex, hasLabelColumn, hasIsDualColumn, dualColumns);
            if (label == SourceLabel.Single)
            {
                if (singleColumns is null)
                {
                    throw new InvalidOperationException("Single-source columns were not found in the evaluation CSV.");
                }

                var truePos = ReadCoords(row, singleColumns.True);
                var predPos = ReadCoords(row, singleColumns.Pred);
                double errorCm = ComputeSingleErrorCm(truePos, predPos);
                rows.Add(new RowData(totalCounts, errorCm, label));
            }
            else
            {
                if (dualColumns is null)
                {
                    throw new InvalidOperationException("Dual-source columns were not found in the evaluation CSV.");
                }

                var truePair = ReadDualCoords(row, dualColumns.True);
                var predPair = ReadDualCoords(row, dualColumns.Pred);
                double errorCm = ComputeDualErrorCm(truePair, predPair);
                rows.Add(new RowData(totalCounts, errorCm, label));
            }
        }

        var singleRows = rows.Where(r => r.Label == SourceLabel.Single).ToList();
        var dualRows = rows.Where(r => r.Label == SourceLabel.Dual).ToList();

        var singleResult = ComputeThreshold(singleRows, errorTargetCm, bins, minSamplesPerBin, "Single");
        var dualResult = ComputeThreshold(dualRows, errorTargetCm, bins, minSamplesPerBin, "Dual");

        var output = new TriggerThresholdOutput(
            singleResult.Threshold,
            dualResult.Threshold,
            errorTargetCm,
            bins,
            minSamplesPerBin,
            Path.GetFileName(evalCsvPath),
            DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            NotesText,
            singleResult.FailureToMeetTarget,
            singleResult.BestMedianErrorAtMaxCounts,
            dualResult.FailureToMeetTarget,
            dualResult.BestMedianErrorAtMaxCounts,
            ConfuseDebounceWindows: 2,
            RecoverDebounceWindows: 2,
            QualityMin: 3.0,
            CheckEveryCounts: 2000,
            MinPublishDurationSeconds: 30.0,
            MaxPublishDurationSeconds: 60.0,
            ThrashWindowCount: 6,
            ThrashChangeThreshold: 3,
            StabilityToleranceCm: 5.0,
            StabilityK: 3,
            EarlyStopProbability: 0.98,
            EarlyStopK: 2,
            PublishProbabilityMin: 0.80);

        var json = JsonSerializer.Serialize(output, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(triggerJsonOutPath, json);

        Console.WriteLine($"Single: rows={singleRows.Count}, minCounts={singleResult.MinCounts:F0}, maxCounts={singleResult.MaxCounts:F0}, threshold={singleResult.Threshold}, medianAtThreshold={singleResult.MedianAtThreshold:F2} cm");
        Console.WriteLine($"Dual: rows={dualRows.Count}, minCounts={dualResult.MinCounts:F0}, maxCounts={dualResult.MaxCounts:F0}, threshold={dualResult.Threshold}, medianAtThreshold={dualResult.MedianAtThreshold:F2} cm");

        return new TriggerThresholdResult(output, singleResult, dualResult);
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

    private static TotalCountsColumn ResolveTotalCountsColumn(CsvTable table)
    {
        if (table.HeaderMap.TryGetValue("TotalCounts", out var totalIndex))
        {
            return new TotalCountsColumn(totalIndex, Array.Empty<int>());
        }

        var channelIndices = new List<int>();
        for (int i = 1; i <= 15; i++)
        {
            string preferred = $"Channel{i}";
            string fallback = $"c{i}";
            if (table.HeaderMap.TryGetValue(preferred, out var idx) || table.HeaderMap.TryGetValue(fallback, out idx))
            {
                channelIndices.Add(idx);
            }
        }

        if (channelIndices.Count != 15)
        {
            throw new InvalidOperationException("Evaluation CSV must contain TotalCounts or a full set of Channel1..Channel15 (or c1..c15) columns.");
        }

        return new TotalCountsColumn(null, channelIndices.ToArray());
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

    private static SourceLabel ResolveLabel(string[] row, int labelIndex, int isDualIndex, bool hasLabelColumn, bool hasIsDualColumn, DualCoordColumns? dualColumns)
    {
        if (hasLabelColumn)
        {
            string value = SafeGet(row, labelIndex).Trim();
            if (value.Length == 0)
            {
                throw new InvalidOperationException("Label column exists but contains an empty value.");
            }

            if (value.Contains("dual", StringComparison.OrdinalIgnoreCase))
            {
                return SourceLabel.Dual;
            }

            if (value.Contains("single", StringComparison.OrdinalIgnoreCase))
            {
                return SourceLabel.Single;
            }

            throw new InvalidOperationException($"Unrecognized Label value '{value}'.");
        }

        if (hasIsDualColumn)
        {
            string value = SafeGet(row, isDualIndex).Trim();
            if (!bool.TryParse(value, out var isDual))
            {
                throw new InvalidOperationException($"IsDual value '{value}' was not recognized as a boolean.");
            }

            return isDual ? SourceLabel.Dual : SourceLabel.Single;
        }

        if (dualColumns is not null)
        {
            var truePair = TryReadDualCoords(row, dualColumns.True);
            if (truePair is not null)
            {
                return SourceLabel.Dual;
            }
        }

        return SourceLabel.Single;
    }

    private static double ReadTotalCounts(CsvTable table, string[] row, TotalCountsColumn totalCountsColumn)
    {
        if (totalCountsColumn.TotalCountsIndex.HasValue)
        {
            string value = SafeGet(row, totalCountsColumn.TotalCountsIndex.Value).Trim();
            return ParseDouble(value, "TotalCounts");
        }

        double sum = 0;
        foreach (var idx in totalCountsColumn.ChannelIndices)
        {
            string value = SafeGet(row, idx).Trim();
            sum += ParseDouble(value, "ChannelCounts");
        }

        return sum;
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

        coords = new CoordColumns(x, y, z);
        return true;
    }

    private static (double X, double Y, double Z) ReadCoords(string[] row, CoordColumns coords)
    {
        double x = ParseDouble(SafeGet(row, coords.X).Trim(), "x");
        double y = ParseDouble(SafeGet(row, coords.Y).Trim(), "y");
        double z = ParseDouble(SafeGet(row, coords.Z).Trim(), "z");
        return (x, y, z);
    }

    private static DualCoords ReadDualCoords(string[] row, DualCoordSet coords)
    {
        var first = ReadCoords(row, coords.First);
        var second = ReadCoords(row, coords.Second);
        return new DualCoords(first, second);
    }

    private static DualCoords? TryReadDualCoords(string[] row, DualCoordSet coords)
    {
        if (!TryReadCoord(row, coords.First, out var first) || !TryReadCoord(row, coords.Second, out var second))
        {
            return null;
        }

        return new DualCoords(first, second);
    }

    private static bool TryReadCoord(string[] row, CoordColumns coords, out (double X, double Y, double Z) value)
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

    private static double ComputeSingleErrorCm((double X, double Y, double Z) truePos, (double X, double Y, double Z) predPos)
    {
        double dx = predPos.X - truePos.X;
        double dy = predPos.Y - truePos.Y;
        double dz = predPos.Z - truePos.Z;
        double distanceMeters = Math.Sqrt(dx * dx + dy * dy + dz * dz);
        return distanceMeters * 100.0;
    }

    private static double ComputeDualErrorCm(DualCoords trueCoords, DualCoords predCoords)
    {
        double option1 = MaxDistanceCm(trueCoords.First, predCoords.First, trueCoords.Second, predCoords.Second);
        double option2 = MaxDistanceCm(trueCoords.First, predCoords.Second, trueCoords.Second, predCoords.First);
        return Math.Min(option1, option2);
    }

    private static double MaxDistanceCm((double X, double Y, double Z) true1, (double X, double Y, double Z) pred1, (double X, double Y, double Z) true2, (double X, double Y, double Z) pred2)
    {
        double d1 = ComputeSingleErrorCm(true1, pred1);
        double d2 = ComputeSingleErrorCm(true2, pred2);
        return Math.Max(d1, d2);
    }

    private static ThresholdGroupResult ComputeThreshold(List<RowData> rows, double errorTargetCm, int bins, int minSamplesPerBin, string label)
    {
        if (rows.Count == 0)
        {
            throw new InvalidOperationException($"No {label} rows were found for threshold computation.");
        }

        var minCounts = rows.Min(r => r.TotalCounts);
        var maxCounts = rows.Max(r => r.TotalCounts);
        var binSummaries = BuildBins(rows, bins, minSamplesPerBin);
        var validBins = binSummaries.Where(b => b.SampleCount >= minSamplesPerBin).OrderBy(b => b.LowerEdge).ToList();

        double threshold = maxCounts;
        double medianAtThreshold = validBins.Count > 0 ? validBins[^1].MedianErrorCm : double.NaN;
        bool failure = true;
        double bestMedianAtMaxCounts = validBins.Count > 0 ? validBins[^1].MedianErrorCm : double.NaN;

        foreach (var bin in validBins)
        {
            bool tailOk = true;
            foreach (var tail in validBins.Where(b => b.LowerEdge >= bin.LowerEdge))
            {
                if (tail.MedianErrorCm > errorTargetCm)
                {
                    tailOk = false;
                    break;
                }
            }

            if (tailOk)
            {
                threshold = bin.LowerEdge;
                medianAtThreshold = bin.MedianErrorCm;
                failure = false;
                break;
            }
        }

        int thresholdRounded = (int)Math.Round(threshold, MidpointRounding.AwayFromZero);
        return new ThresholdGroupResult(thresholdRounded, minCounts, maxCounts, medianAtThreshold, failure, bestMedianAtMaxCounts);
    }

    private static List<BinSummary> BuildBins(List<RowData> rows, int bins, int minSamplesPerBin)
    {
        if (bins <= 0)
        {
            throw new ArgumentException("--bins must be positive.");
        }

        var sortedCounts = rows.Select(r => r.TotalCounts).OrderBy(v => v).ToArray();
        var edges = new double[Math.Max(0, bins - 1)];
        for (int i = 1; i < bins; i++)
        {
            edges[i - 1] = Quantile(sortedCounts, (double)i / bins);
        }

        var binRows = new List<RowData>[bins];
        for (int i = 0; i < bins; i++)
        {
            binRows[i] = new List<RowData>();
        }

        foreach (var row in rows)
        {
            int binIndex = FindBinIndex(row.TotalCounts, edges);
            binRows[binIndex].Add(row);
        }

        var summaries = new List<BinSummary>();
        double lowerEdge = sortedCounts.Length > 0 ? sortedCounts[0] : 0;
        for (int i = 0; i < bins; i++)
        {
            double upperEdge = i < edges.Length ? edges[i] : double.PositiveInfinity;
            var errors = binRows[i].Select(r => r.ErrorCm).ToList();
            double median = errors.Count > 0 ? Median(errors) : double.NaN;
            summaries.Add(new BinSummary(lowerEdge, upperEdge, errors.Count, median));
            lowerEdge = upperEdge;
        }

        return summaries;
    }

    private static int FindBinIndex(double value, double[] edges)
    {
        for (int i = 0; i < edges.Length; i++)
        {
            if (value <= edges[i])
            {
                return i;
            }
        }

        return edges.Length;
    }

    private static double Quantile(double[] sorted, double q)
    {
        if (sorted.Length == 0)
        {
            return double.NaN;
        }

        if (sorted.Length == 1)
        {
            return sorted[0];
        }

        double position = q * (sorted.Length - 1);
        int lower = (int)Math.Floor(position);
        int upper = (int)Math.Ceiling(position);
        if (lower == upper)
        {
            return sorted[lower];
        }

        double weight = position - lower;
        return sorted[lower] + (sorted[upper] - sorted[lower]) * weight;
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

    private sealed record CsvTable(string[] Headers, List<string[]> Rows)
    {
        public Dictionary<string, int> HeaderMap { get; } = Headers
            .Select((h, idx) => (Header: h.Trim(), Index: idx))
            .ToDictionary(h => h.Header, h => h.Index, StringComparer.OrdinalIgnoreCase);
    }

    private sealed record TotalCountsColumn(int? TotalCountsIndex, int[] ChannelIndices);

    private sealed record CoordColumns(int X, int Y, int Z);

    private sealed record SingleCoordColumns(CoordColumns True, CoordColumns Pred);

    private sealed record DualCoordSet(CoordColumns First, CoordColumns Second);

    private sealed record DualCoordColumns(DualCoordSet True, DualCoordSet Pred);

    private sealed record DualCoords((double X, double Y, double Z) First, (double X, double Y, double Z) Second);

    private sealed record RowData(double TotalCounts, double ErrorCm, SourceLabel Label);

    private sealed record BinSummary(double LowerEdge, double UpperEdge, int SampleCount, double MedianErrorCm);

    private enum SourceLabel
    {
        Single,
        Dual
    }
}

internal sealed record TriggerThresholdOutput(
    int Nmin_15cm_30s,
    int Nmin_15cm_60s,
    double error_target_cm,
    int bins,
    int min_samples_per_bin,
    string generated_from,
    string generated_at_utc,
    string notes,
    bool single_failure_to_meet_target,
    double single_best_median_error_cm,
    bool dual_failure_to_meet_target,
    double dual_best_median_error_cm,
    int ConfuseDebounceWindows,
    int RecoverDebounceWindows,
    double QualityMin,
    int CheckEveryCounts,
    double MinPublishDurationSeconds,
    double MaxPublishDurationSeconds,
    int ThrashWindowCount,
    int ThrashChangeThreshold,
    double StabilityToleranceCm,
    int StabilityK,
    double EarlyStopProbability,
    int EarlyStopK,
    double PublishProbabilityMin);

internal sealed record ThresholdGroupResult(int Threshold, double MinCounts, double MaxCounts, double MedianAtThreshold, bool FailureToMeetTarget, double BestMedianErrorAtMaxCounts);

internal sealed record TriggerThresholdResult(TriggerThresholdOutput Output, ThresholdGroupResult Single, ThresholdGroupResult Dual);
