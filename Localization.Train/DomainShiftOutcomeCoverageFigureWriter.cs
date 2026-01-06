using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Localization.ML;
using OxyPlot;
using OxyPlot.Axes;
using OxyPlot.Legends;
using OxyPlot.Series;
using OxyPlot.SkiaSharp;

namespace Localization.Train;

public static class DomainShiftOutcomeCoverageFigureWriter
{
    public static void Write(
        string baselineDataDir,
        string domainShiftDataDir,
        string outPngPath,
        double? durationOverride = null,
        string? artifactsDir = null,
        IReadOnlyList<string>? domainShiftSingleFiles = null,
        IReadOnlyList<string>? domainShiftDualFiles = null)
    {
        string artifactDirectory = string.IsNullOrWhiteSpace(artifactsDir) ? "artifacts" : artifactsDir;
        var pipeline = LocalizationPipeline.Load(artifactDirectory);
        Write(pipeline, baselineDataDir, domainShiftDataDir, outPngPath, durationOverride, domainShiftSingleFiles, domainShiftDualFiles);
    }

    public static void Write(
        LocalizationPipeline pipeline,
        string baselineDataDir,
        string domainShiftDataDir,
        string outPngPath,
        double? durationOverride = null,
        IReadOnlyList<string>? domainShiftSingleFiles = null,
        IReadOnlyList<string>? domainShiftDualFiles = null)
    {
        if (!Directory.Exists(baselineDataDir))
        {
            Console.WriteLine($"Warning: baseline data directory {baselineDataDir} does not exist. Skipping domain-shift outcome coverage figure.");
            return;
        }

        if (!Directory.Exists(domainShiftDataDir))
        {
            Console.WriteLine($"Warning: domain-shift data directory {domainShiftDataDir} does not exist. Skipping domain-shift outcome coverage figure.");
            return;
        }

        var baselineSingles = Program.LoadSingleGroups(baselineDataDir, durationOverride);
        var baselineDuals = Program.LoadDualGroups(baselineDataDir, durationOverride);

        var baselineSingleFiles = CollectGroupFiles(baselineDataDir, Program.SingleGroups);
        var baselineDualFiles = CollectGroupFiles(baselineDataDir, Program.DualGroups);

        var domainShiftSingles = LoadSingleRows(domainShiftDataDir, durationOverride, domainShiftSingleFiles, baselineSingleFiles);
        var domainShiftDuals = LoadDualRows(domainShiftDataDir, durationOverride, domainShiftDualFiles, baselineDualFiles);

        if (baselineSingles.Count == 0 && baselineDuals.Count == 0)
        {
            Console.WriteLine("Warning: no baseline rows found. Skipping domain-shift outcome coverage figure.");
            return;
        }

        if (domainShiftSingles.Count == 0 && domainShiftDuals.Count == 0)
        {
            Console.WriteLine("Warning: no domain-shift rows found. Skipping domain-shift outcome coverage figure.");
            return;
        }

        var baselineCounts = CountOutcomes(pipeline, baselineSingles.Concat(baselineDuals));
        var domainShiftCounts = CountOutcomes(pipeline, domainShiftSingles.Concat(domainShiftDuals));

        var outcomeOrder = BuildOutcomeOrder(baselineCounts, domainShiftCounts);
        var colorMap = BuildColorMap(outcomeOrder);

        double baselineTotal = baselineCounts.Values.Sum();
        double domainShiftTotal = domainShiftCounts.Values.Sum();

        var model = new PlotModel
        {
            Title = "Outcome coverage: baseline vs domain shift",
        };

        model.Legends.Add(new Legend
        {
            LegendPlacement = LegendPlacement.Outside,
            LegendPosition = LegendPosition.RightTop,
            LegendBorderThickness = 0
        });

        model.Axes.Add(new CategoryAxis
        {
            Position = AxisPosition.Left,
            Labels = { "Baseline", "Domain shift" }
        });

        model.Axes.Add(new LinearAxis
        {
            Position = AxisPosition.Bottom,
            Minimum = 0,
            Maximum = 100,
            Title = "Fraction of samples (%)",
            MajorGridlineStyle = LineStyle.Solid,
            MinorGridlineStyle = LineStyle.None
        });

        foreach (var label in outcomeOrder)
        {
            baselineCounts.TryGetValue(label, out int baselineCount);
            domainShiftCounts.TryGetValue(label, out int domainShiftCount);

            double baselinePct = baselineTotal == 0 ? 0 : baselineCount * 100.0 / baselineTotal;
            double domainShiftPct = domainShiftTotal == 0 ? 0 : domainShiftCount * 100.0 / domainShiftTotal;

            var color = colorMap[label];
            var series = new BarSeries
            {
                Title = label,
                IsStacked = true,
                FillColor = color,
                StrokeColor = color,
                StrokeThickness = 1.5
            };

            series.Items.Add(new BarItem { Value = baselinePct });
            series.Items.Add(new BarItem { Value = domainShiftPct });

            model.Series.Add(series);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outPngPath) ?? ".");
        using var stream = File.Create(outPngPath);
        var exporter = new PngExporter { Width = 900, Height = 600 };
        exporter.Export(model, stream);
    }

    private static IReadOnlyList<string> CollectGroupFiles(string dataDir, IReadOnlyList<string> groups)
    {
        var list = new List<string>();
        foreach (var group in groups)
        {
            list.AddRange(Program.FindFilesByPrefix(dataDir, group));
        }

        return list;
    }

    private static IReadOnlyList<LocalizationRow> LoadSingleRows(
        string dataDir,
        double? durationOverride,
        IReadOnlyList<string>? overrideFiles,
        IReadOnlyList<string> baselineFiles)
    {
        var filesToUse = NormalizePaths(dataDir, overrideFiles, baselineFiles);
        if (filesToUse.Count == 0)
        {
            return Program.LoadSingleGroups(dataDir, durationOverride);
        }

        var rows = new List<LocalizationRow>();
        foreach (var path in filesToUse)
        {
            if (!File.Exists(path))
            {
                Console.WriteLine($"Warning: single-source file {path} not found. Skipping.");
                continue;
            }

            rows.AddRange(DatasetLoader.LoadSingleSource(path, durationOverride));
        }

        return rows;
    }

    private static IReadOnlyList<LocalizationRow> LoadDualRows(
        string dataDir,
        double? durationOverride,
        IReadOnlyList<string>? overrideFiles,
        IReadOnlyList<string> baselineFiles)
    {
        var filesToUse = NormalizePaths(dataDir, overrideFiles, baselineFiles);
        if (filesToUse.Count == 0)
        {
            return Program.LoadDualGroups(dataDir, durationOverride);
        }

        var rows = new List<LocalizationRow>();
        foreach (var path in filesToUse)
        {
            if (!File.Exists(path))
            {
                Console.WriteLine($"Warning: dual-source file {path} not found. Skipping.");
                continue;
            }

            var name = Path.GetFileNameWithoutExtension(path);
            string? group = Program.DualGroups.FirstOrDefault(g => name.StartsWith(g, StringComparison.OrdinalIgnoreCase));
            if (group == null)
            {
                Console.WriteLine($"Warning: could not determine dual group for {path}. Skipping.");
                continue;
            }

            if (!Program.PairMetadataGroups.TryGetValue(group, out var metadataPrefix))
            {
                Console.WriteLine($"Warning: missing metadata mapping for dual group {group}. Skipping {path}.");
                continue;
            }

            var metadataFiles = Program.FindFilesByPrefix(dataDir, metadataPrefix);
            if (metadataFiles.Count == 0)
            {
                Console.WriteLine($"Warning: missing metadata files for dual group {group} in {dataDir}. Skipping {path}.");
                continue;
            }

            rows.AddRange(DatasetLoader.LoadDualSource(path, metadataFiles[0], durationOverride));
        }

        return rows;
    }

    private static IReadOnlyList<string> NormalizePaths(string dataDir, IReadOnlyList<string>? overrideFiles, IReadOnlyList<string> baselineFiles)
    {
        var paths = new List<string>();
        var candidates = (overrideFiles != null && overrideFiles.Count > 0)
            ? overrideFiles.Where(c => !string.IsNullOrWhiteSpace(c)).ToList()
            : baselineFiles.Select(Path.GetFileName).Where(n => !string.IsNullOrWhiteSpace(n)).ToList();

        foreach (var candidate in candidates)
        {
            var fullPath = Path.IsPathRooted(candidate)
                ? candidate
                : Path.Combine(dataDir, candidate);

            if (!paths.Contains(fullPath, StringComparer.OrdinalIgnoreCase))
            {
                paths.Add(fullPath);
            }
        }

        return paths;
    }

    private static Dictionary<string, int> CountOutcomes(LocalizationPipeline pipeline, IEnumerable<LocalizationRow> rows)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            var prediction = pipeline.Predict(row);
            string label = string.IsNullOrWhiteSpace(prediction.Label) ? "Unknown" : prediction.Label;

            if (!counts.TryGetValue(label, out var current))
            {
                current = 0;
            }

            counts[label] = current + 1;
        }

        return counts;
    }

    private static List<string> BuildOutcomeOrder(Dictionary<string, int> baselineCounts, Dictionary<string, int> domainShiftCounts)
    {
        var defaultOrder = new List<string>
        {
            "Single",
            "Dual",
            "Single (centroid override)",
            "Unknown",
            "Refuse",
            "Fallback"
        };

        var observed = new HashSet<string>(baselineCounts.Keys.Concat(domainShiftCounts.Keys), StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var label in defaultOrder)
        {
            if (observed.Contains(label))
            {
                order.Add(label);
            }
        }

        var extras = observed.Except(order, StringComparer.Ordinal).OrderBy(l => l, StringComparer.Ordinal);
        order.AddRange(extras);
        return order;
    }

    private static Dictionary<string, OxyColor> BuildColorMap(IReadOnlyList<string> labels)
    {
        var colors = new Dictionary<string, OxyColor>(StringComparer.Ordinal)
        {
            { "Single", OxyColors.SteelBlue },
            { "Dual", OxyColors.ForestGreen },
            { "Single (centroid override)", OxyColors.Goldenrod },
            { "Unknown", OxyColors.Gray },
            { "Refuse", OxyColors.IndianRed },
            { "Fallback", OxyColors.DarkOrange }
        };

        var palette = OxyPalettes.Hot(labels.Count + 2).Colors;
        int paletteIndex = 0;
        foreach (var label in labels)
        {
            if (!colors.ContainsKey(label))
            {
                colors[label] = palette[paletteIndex % palette.Count];
                paletteIndex++;
            }
        }

        return colors;
    }
}
