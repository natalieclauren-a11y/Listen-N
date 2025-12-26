using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;

namespace Localization.ML;

public static class Grouping
{
    private const int CoordinateRoundingDecimals = 2;

    public static string GetGroupId(LocalizationRow row)
    {
        if (!row.IsDual && row.SingleCoordinates is { Length: 3 } single)
        {
            return "S:" + string.Join(":", single.Select(FormatCoordinate));
        }

        if (row.IsDual && row.DualCoordinates is { Length: 6 } dual)
        {
            var first = new[] { dual[0], dual[1], dual[2] }.Select(FormatCoordinate).ToArray();
            var second = new[] { dual[3], dual[4], dual[5] }.Select(FormatCoordinate).ToArray();

            var triplets = new[]
            {
                string.Join(":", first),
                string.Join(":", second)
            };

            Array.Sort(triplets, StringComparer.Ordinal);
            return "D:" + string.Join("|", triplets);
        }

        if (row.Metadata is { Count: > 0 } metadata)
        {
            var parts = metadata
                .OrderBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase)
                .Select(kvp => $"{kvp.Key}={kvp.Value}");
            return "M:" + string.Join(";", parts);
        }

        throw new InvalidOperationException("Grouped split cannot be computed because no scenario identifier exists for this row.");
    }

    public static (List<LocalizationRow> Train, List<LocalizationRow> Holdout) GroupSplit(IEnumerable<LocalizationRow> rows, double holdoutFraction, int seed)
    {
        var grouped = rows.ToDictionary(r => r, GetGroupId);
        var groupIds = grouped.Values.Distinct().ToList();

        var rng = new Random(seed);
        groupIds = groupIds.OrderBy(_ => rng.Next()).ToList();

        int holdoutCount = (int)Math.Round(groupIds.Count * holdoutFraction);
        holdoutCount = Math.Min(Math.Max(1, holdoutCount), groupIds.Count);

        var holdoutGroups = new HashSet<string>(groupIds.Take(holdoutCount));
        var trainGroups = new HashSet<string>(groupIds.Skip(holdoutCount));

        var trainRows = new List<LocalizationRow>();
        var holdoutRows = new List<LocalizationRow>();

        foreach (var kvp in grouped)
        {
            if (holdoutGroups.Contains(kvp.Value))
            {
                holdoutRows.Add(kvp.Key);
            }
            else
            {
                trainRows.Add(kvp.Key);
            }
        }

        Debug.Assert(!trainRows.Any(r => holdoutGroups.Contains(grouped[r])), "Group leakage detected between train and holdout sets.");
        Debug.Assert(!holdoutRows.Any(r => trainGroups.Contains(grouped[r])), "Group leakage detected between train and holdout sets.");

        return (trainRows, holdoutRows);
    }

    private static string FormatCoordinate(double value)
    {
        var rounded = Math.Round(value, CoordinateRoundingDecimals);
        return rounded.ToString("F2", CultureInfo.InvariantCulture);
    }
}
