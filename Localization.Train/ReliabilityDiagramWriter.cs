using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.ML;
using ScottPlot;

namespace Localization.Train;

public static class ReliabilityDiagramWriter
{
    private sealed class ScoredRow
    {
        public bool Label { get; set; }

        public float Probability { get; set; }
    }

    private sealed record CalibrationBin(double MeanProbability, double FractionPositive, int Count);

    private sealed record CalibrationResult(IReadOnlyList<CalibrationBin> Bins, double BrierScore, double ExpectedCalibrationError, int Count);

    public static void WriteBinaryReliabilityDiagram(
        MLContext mlContext,
        IDataView scoredData,
        string outputPath,
        int binCount = 10)
    {
        var calibration = ComputeCalibration(mlContext, scoredData, binCount);
        if (calibration.Count == 0)
        {
            return;
        }

        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        double[] xs = calibration.Bins.Select(b => b.MeanProbability).ToArray();
        double[] ys = calibration.Bins.Select(b => b.FractionPositive).ToArray();

        var plot = new Plot();
        plot.Title("Reliability diagram (holdout)");
        plot.XLabel("Mean predicted probability (Dual)");
        plot.YLabel("Empirical frequency (Dual)");

        var scatter = plot.Add.Scatter(xs, ys);
        scatter.MarkerSize = 7;
        scatter.LineStyle.Width = 2;

        var diagonal = plot.Add.Line(0, 0, 1, 1);
        diagonal.LineStyle.Pattern = LinePattern.Dash;
        diagonal.LineStyle.Width = 1;
        diagonal.Color = Colors.Gray;

        plot.Axes.SetLimits(0, 1, 0, 1);

        plot.SavePng(outputPath, 900, 600);

        Console.WriteLine($"Calibration: Brier={calibration.BrierScore:F4}, ECE={calibration.ExpectedCalibrationError:F4}, bins={binCount}, N={calibration.Count}");
    }

    public static (double BrierScore, double ExpectedCalibrationError, int Count) ComputeBinaryCalibrationMetrics(
        MLContext mlContext,
        IDataView scoredData,
        int binCount = 10)
    {
        var calibration = ComputeCalibration(mlContext, scoredData, binCount);
        return (calibration.BrierScore, calibration.ExpectedCalibrationError, calibration.Count);
    }

    private static CalibrationResult ComputeCalibration(MLContext mlContext, IDataView scoredData, int binCount)
    {
        var rows = mlContext.Data.CreateEnumerable<ScoredRow>(scoredData, reuseRowObject: false).ToList();
        if (rows.Count == 0)
        {
            return new CalibrationResult(Array.Empty<CalibrationBin>(), double.NaN, double.NaN, 0);
        }

        var bins = new List<CalibrationBin>(binCount);
        double brierSum = 0;

        var grouped = Enumerable.Range(0, binCount).ToDictionary(i => i, _ => new List<ScoredRow>());
        foreach (var row in rows)
        {
            double p = Math.Clamp(row.Probability, 0f, 1f);
            int bin = Math.Min(binCount - 1, (int)Math.Floor(p * binCount));
            grouped[bin].Add(row);
            double y = row.Label ? 1.0 : 0.0;
            brierSum += Math.Pow(p - y, 2);
        }

        foreach (var kvp in grouped)
        {
            if (kvp.Value.Count == 0)
            {
                continue;
            }

            double meanP = kvp.Value.Average(r => r.Probability);
            double fracPos = kvp.Value.Average(r => r.Label ? 1.0 : 0.0);
            bins.Add(new CalibrationBin(meanP, fracPos, kvp.Value.Count));
        }

        double brier = brierSum / rows.Count;
        double ece = 0;
        foreach (var bin in bins)
        {
            ece += (bin.Count / (double)rows.Count) * Math.Abs(bin.FractionPositive - bin.MeanProbability);
        }

        return new CalibrationResult(bins, brier, ece, rows.Count);
    }
}
