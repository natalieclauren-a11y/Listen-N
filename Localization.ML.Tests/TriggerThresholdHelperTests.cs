using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using Localization.Train;
using Xunit;

namespace Localization.ML.Tests;

public class TriggerThresholdHelperTests
{
    [Fact]
    public void ComputeAndWrite_ReconstructsCountsAndSelectsThresholds()
    {
        using var tempDir = Directory.CreateTempSubdirectory();
        string csvPath = Path.Combine(tempDir.FullName, "eval.csv");
        string jsonPath = Path.Combine(tempDir.FullName, "trigger_policy.json");

        var header = string.Join(',', new[]
        {
            "Label",
            string.Join(',', Enumerable.Range(1, 15).Select(i => $"Channel{i}")),
            "x","y","z","pred_x","pred_y","pred_z",
            "x1","y1","z1","x2","y2","z2","pred_x1","pred_y1","pred_z1","pred_x2","pred_y2","pred_z2"
        });

        var rows = new[]
        {
            BuildSingleRow(100, 0.3, "Single"),
            BuildSingleRow(200, 0.2, "Single"),
            BuildSingleRow(300, 0.1, "Single"),
            BuildSingleRow(400, 0.05, "Single"),
            BuildDualRow(150, 0.3, "Dual"),
            BuildDualRow(350, 0.0, "Dual", swapPredictions: true)
        };

        File.WriteAllLines(csvPath, new[] { header }.Concat(rows));

        var result = TriggerThresholdHelper.ComputeAndWrite(csvPath, jsonPath, 15.0, 2, 1);

        Assert.Equal(250, result.Output.N_min_15cm_single);
        Assert.Equal(250, result.Output.N_min_15cm_dual);
        Assert.False(result.Single.FailureToMeetTarget);
        Assert.False(result.Dual.FailureToMeetTarget);
        Assert.Equal(0.0, result.Dual.MedianAtThreshold);
        Assert.True(File.Exists(jsonPath));

        using var json = JsonDocument.Parse(File.ReadAllText(jsonPath));
        Assert.Equal(250, json.RootElement.GetProperty("N_min_15cm_single").GetInt32());
        Assert.Equal(250, json.RootElement.GetProperty("N_min_15cm_dual").GetInt32());
        Assert.Equal(15.0, json.RootElement.GetProperty("error_target_cm").GetDouble(), 3);
    }

    private static string BuildSingleRow(int totalCounts, double predOffsetMeters, string label)
    {
        var channels = BuildChannels(totalCounts);
        string coords = string.Join(',', new[]
        {
            Format(label),
            channels,
            "0","0","0",
            Format(predOffsetMeters),"0","0",
            "", "", "", "", "", "", "", "", "", "", "", ""
        });

        return coords;
    }

    private static string BuildDualRow(int totalCounts, double offsetMeters, string label, bool swapPredictions = false)
    {
        var channels = BuildChannels(totalCounts);
        var true1 = new[] { "0", "0", "0" };
        var true2 = new[] { "1", "0", "0" };
        string[] pred1;
        string[] pred2;
        if (swapPredictions)
        {
            pred1 = true2;
            pred2 = true1;
        }
        else
        {
            pred1 = new[] { Format(offsetMeters), "0", "0" };
            pred2 = new[] { Format(1 + offsetMeters), "0", "0" };
        }

        string row = string.Join(',', new[]
        {
            Format(label),
            channels,
            "", "", "", "", "", "",
            string.Join(',', true1),
            string.Join(',', true2),
            string.Join(',', pred1),
            string.Join(',', pred2)
        });

        return row;
    }

    private static string BuildChannels(int totalCounts)
    {
        string[] channels = new string[15];
        channels[0] = totalCounts.ToString(CultureInfo.InvariantCulture);
        for (int i = 1; i < channels.Length; i++)
        {
            channels[i] = "0";
        }

        return string.Join(',', channels);
    }

    private static string Format(string value) => value;

    private static string Format(double value) => value.ToString(CultureInfo.InvariantCulture);
}
