using System.Globalization;
using System.Text.Json;
using Localization.Metrics;

namespace Localization.Metrics.Cli;

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public static int Main(string[] args)
    {
        var parsed = ParseArgs(args);
        if (parsed is null)
        {
            Console.Error.WriteLine("Usage: --truth runTruth.json --windows windows.json --output report.json [--options options.json] [--table table.csv]");
            return 1;
        }

        var runTruth = JsonSerializer.Deserialize<RunTruth>(File.ReadAllText(parsed.TruthPath), JsonOptions);
        if (runTruth is null)
        {
            Console.Error.WriteLine("Failed to parse run truth JSON.");
            return 1;
        }

        var windowRecords = JsonSerializer.Deserialize<List<WindowRecord>>(File.ReadAllText(parsed.WindowsPath), JsonOptions) ?? new List<WindowRecord>();
        var options = parsed.OptionsPath is null
            ? new Phase4Options()
            : JsonSerializer.Deserialize<Phase4Options>(File.ReadAllText(parsed.OptionsPath), JsonOptions) ?? new Phase4Options();

        var metrics = new Phase4Metrics();
        var report = metrics.ComputeAll(runTruth, windowRecords, options);

        File.WriteAllText(parsed.OutputPath, JsonSerializer.Serialize(report, JsonOptions));
        if (parsed.TablePath is not null)
        {
            WriteCsv(parsed.TablePath, report.WindowMetrics);
        }

        Console.WriteLine($"Phase 4 metrics written to {Path.GetFullPath(parsed.OutputPath)}");
        return 0;
    }

    private static void WriteCsv(string path, IReadOnlyList<WindowMetricsRow> rows)
    {
        using var writer = new StreamWriter(path);
        var headers = new[]
        {
            "run_id",
            "window_index",
            "t_start",
            "t_end",
            "duration_s",
            "total_counts",
            "cumulative_counts",
            "cumulative_time_s",
            "predicted_label",
            "is_ood",
            "probability",
            "true_label",
            "true_coords",
            "error_cm",
            "mean_error_cm",
            "e1_cm",
            "e2_cm",
            "centroid_error_cm",
            "bias_x",
            "bias_y",
            "bias_z",
            "bias1_x",
            "bias1_y",
            "bias1_z",
            "bias2_x",
            "bias2_y",
            "bias2_z",
            "centroid_bias_x",
            "centroid_bias_y",
            "centroid_bias_z",
            "true_distance_cm",
            "stable_flag",
            "mahalanobis_distance",
            "rt_y",
            "rt_sigma_y",
            "rt_selected_gate",
            "rt_correlation_time",
            "rt_state_label"
        };

        writer.WriteLine(string.Join(',', headers));
        foreach (var row in rows)
        {
            var values = new[]
            {
                row.RunId,
                row.WindowIndex.ToString(CultureInfo.InvariantCulture),
                row.TStart.ToString(CultureInfo.InvariantCulture),
                row.TEnd.ToString(CultureInfo.InvariantCulture),
                row.DurationSeconds.ToString(CultureInfo.InvariantCulture),
                row.TotalCounts.ToString(CultureInfo.InvariantCulture),
                row.CumulativeCounts.ToString(CultureInfo.InvariantCulture),
                row.CumulativeTimeSeconds.ToString(CultureInfo.InvariantCulture),
                row.PredictedLabel,
                row.IsOod ? "true" : "false",
                row.Probability.ToString(CultureInfo.InvariantCulture),
                row.TrueLabel,
                string.Join('|', row.TrueCoords.Select(c => c.ToString(CultureInfo.InvariantCulture))),
                FormatNullable(row.ErrorCm),
                FormatNullable(row.MeanErrorCm),
                FormatNullable(row.Error1Cm),
                FormatNullable(row.Error2Cm),
                FormatNullable(row.CentroidErrorCm),
                FormatNullable(row.BiasX),
                FormatNullable(row.BiasY),
                FormatNullable(row.BiasZ),
                FormatNullable(row.Bias1X),
                FormatNullable(row.Bias1Y),
                FormatNullable(row.Bias1Z),
                FormatNullable(row.Bias2X),
                FormatNullable(row.Bias2Y),
                FormatNullable(row.Bias2Z),
                FormatNullable(row.CentroidBiasX),
                FormatNullable(row.CentroidBiasY),
                FormatNullable(row.CentroidBiasZ),
                FormatNullable(row.TrueDistanceCm),
                row.StableFlag ? "true" : "false",
                FormatNullable(row.MahalanobisDistance),
                FormatNullable(row.RtMetrics?.Y),
                FormatNullable(row.RtMetrics?.SigmaY),
                FormatNullable(row.RtMetrics?.SelectedGate),
                FormatNullable(row.RtMetrics?.CorrelationTimeEstimate),
                row.RtMetrics?.StateLabel ?? string.Empty
            };

            writer.WriteLine(string.Join(',', values.Select(EscapeCsv)));
        }
    }

    private static string EscapeCsv(string value)
    {
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n'))
        {
            return '"' + value.Replace("\"", "\"\"") + '"';
        }

        return value;
    }

    private static string FormatNullable(double? value)
    {
        return value.HasValue ? value.Value.ToString(CultureInfo.InvariantCulture) : string.Empty;
    }

    private static ParsedArgs? ParseArgs(string[] args)
    {
        string? truth = null;
        string? windows = null;
        string? output = null;
        string? options = null;
        string? table = null;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--truth":
                    truth = NextValue(args, ref i);
                    break;
                case "--windows":
                    windows = NextValue(args, ref i);
                    break;
                case "--output":
                    output = NextValue(args, ref i);
                    break;
                case "--options":
                    options = NextValue(args, ref i);
                    break;
                case "--table":
                    table = NextValue(args, ref i);
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(truth) || string.IsNullOrWhiteSpace(windows) || string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        return new ParsedArgs(truth, windows, output, options, table);
    }

    private static string? NextValue(string[] args, ref int index)
    {
        if (index + 1 >= args.Length)
        {
            return null;
        }

        index++;
        return args[index];
    }

    private sealed record ParsedArgs(string TruthPath, string WindowsPath, string OutputPath, string? OptionsPath, string? TablePath);
}
