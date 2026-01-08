using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using Localization.Train;
using Xunit;

namespace Localization.ML.Tests;

public class TriggerPolicyWriterTests
{
    [Fact]
    public void ComputeAndWrite_WritesValidPolicyJson()
    {
        var tempDir = Directory.CreateTempSubdirectory();
        try
        {
            string csvPath = Path.Combine(tempDir.FullName, "eval.csv");
            string jsonPath = Path.Combine(tempDir.FullName, "trigger_policy.json");

            File.WriteAllLines(csvPath, BuildCsvLines());

            var output = TriggerPolicyWriter.ComputeAndWrite(
                csvPath,
                jsonPath,
                0.15,
                new[] { 30, 60 },
                100,
                1,
                "unit test",
                "single");

            Assert.True(File.Exists(jsonPath));

            using var document = JsonDocument.Parse(File.ReadAllText(jsonPath));
            var root = document.RootElement;
            Assert.True(root.TryGetProperty("schema_version", out _));
            Assert.True(root.TryGetProperty("created_utc", out _));
            Assert.True(root.TryGetProperty("metric_definition", out _));
            Assert.True(root.TryGetProperty("thresholds", out _));

            int nmin30 = root.GetProperty("thresholds").GetProperty("30").GetProperty("nmin_15cm_single").GetInt32();
            Assert.Equal(200, nmin30);
            Assert.Equal(200, output.Thresholds["30"].Nmin15cmSingle);
        }
        finally
        {
            Directory.Delete(tempDir.FullName, true);
        }
    }

    [Fact]
    public void ComputeAndWrite_IsMonotonicWithStricterTarget()
    {
        var tempDir = Directory.CreateTempSubdirectory();
        try
        {
            string csvPath = Path.Combine(tempDir.FullName, "eval.csv");
            string jsonPathLoose = Path.Combine(tempDir.FullName, "trigger_policy_015.json");
            string jsonPathStrict = Path.Combine(tempDir.FullName, "trigger_policy_010.json");

            File.WriteAllLines(csvPath, BuildCsvLines());

            var loose = TriggerPolicyWriter.ComputeAndWrite(
                csvPath,
                jsonPathLoose,
                0.15,
                new[] { 30, 60 },
                100,
                1,
                null,
                "single");

            var strict = TriggerPolicyWriter.ComputeAndWrite(
                csvPath,
                jsonPathStrict,
                0.10,
                new[] { 30, 60 },
                100,
                1,
                null,
                "single");

            Assert.True(strict.Thresholds["30"].Nmin15cmSingle >= loose.Thresholds["30"].Nmin15cmSingle);
            Assert.True(strict.Thresholds["60"].Nmin15cmSingle >= loose.Thresholds["60"].Nmin15cmSingle);
        }
        finally
        {
            Directory.Delete(tempDir.FullName, true);
        }
    }

    private static string[] BuildCsvLines()
    {
        var header = string.Join(',', new[]
        {
            "Duration_s",
            string.Join(',', Enumerable.Range(1, 15).Select(i => $"Channel{i}")),
            "x", "y", "z", "pred_x", "pred_y", "pred_z"
        });

        var rows = new[]
        {
            BuildSingleRow(30, 100, 0.4),
            BuildSingleRow(30, 200, 0.2),
            BuildSingleRow(30, 300, 0.12),
            BuildSingleRow(30, 400, 0.05),
            BuildSingleRow(60, 150, 0.3),
            BuildSingleRow(60, 250, 0.14),
            BuildSingleRow(60, 350, 0.08)
        };

        return new[] { header }.Concat(rows).ToArray();
    }

    private static string BuildSingleRow(int durationSeconds, int totalCounts, double predOffsetMeters)
    {
        string[] channels = new string[15];
        channels[0] = totalCounts.ToString(CultureInfo.InvariantCulture);
        for (int i = 1; i < channels.Length; i++)
        {
            channels[i] = "0";
        }

        return string.Join(',', new[]
        {
            durationSeconds.ToString(CultureInfo.InvariantCulture),
            string.Join(',', channels),
            "0", "0", "0",
            predOffsetMeters.ToString(CultureInfo.InvariantCulture), "0", "0"
        });
    }
}
