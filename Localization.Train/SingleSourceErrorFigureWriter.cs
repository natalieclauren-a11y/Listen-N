using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OxyPlot;
using OxyPlot.Axes;
using OxyPlot.Series;
using OxyPlot.SkiaSharp;

namespace Localization.Train;

internal static class SingleSourceErrorFigureWriter
{
    public static void Write(
        string outputPath,
        IReadOnlyList<double> errorsCm,
        string title,
        string subtitle)
    {
        if (errorsCm == null)
        {
            throw new ArgumentNullException(nameof(errorsCm));
        }

        var sorted = errorsCm.OrderBy(e => e).ToList();
        if (sorted.Count == 0)
        {
            throw new ArgumentException("No error samples provided", nameof(errorsCm));
        }

        var model = new PlotModel
        {
            Title = title,
            Subtitle = subtitle
        };

        var xAxis = new LinearAxis
        {
            Position = AxisPosition.Bottom,
            Title = "Localization error (cm)",
            Minimum = 0,
            MajorGridlineStyle = LineStyle.Dash,
            MinorGridlineStyle = LineStyle.Dot
        };

        var yAxis = new LinearAxis
        {
            Position = AxisPosition.Left,
            Title = "Cumulative probability",
            Minimum = 0,
            Maximum = 1,
            MajorGridlineStyle = LineStyle.Dash,
            MinorGridlineStyle = LineStyle.Dot
        };

        model.Axes.Add(xAxis);
        model.Axes.Add(yAxis);

        var cdfSeries = new LineSeries
        {
            Color = OxyColors.SteelBlue,
            StrokeThickness = 2
        };

        for (int i = 0; i < sorted.Count; i++)
        {
            double probability = (i + 1d) / sorted.Count;
            cdfSeries.Points.Add(new DataPoint(sorted[i], probability));
        }

        model.Series.Add(cdfSeries);

        AddQuantileLine(model, sorted, 0.5, OxyColors.Gray);
        AddQuantileLine(model, sorted, 0.9, OxyColors.DarkSlateBlue);
        AddQuantileLine(model, sorted, 0.95, OxyColors.IndianRed);

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? ".");
        using var stream = File.Open(outputPath, FileMode.Create);
        new PngExporter { Width = 900, Height = 600 }.Export(model, stream);
    }

    private static void AddQuantileLine(PlotModel model, IReadOnlyList<double> sorted, double percentile, OxyColor color)
    {
        double position = percentile * (sorted.Count - 1);
        int lower = (int)Math.Floor(position);
        int upper = (int)Math.Ceiling(position);
        double x = lower == upper
            ? sorted[lower]
            : sorted[lower] + (position - lower) * (sorted[upper] - sorted[lower]);

        var line = new LineSeries
        {
            Color = color,
            StrokeThickness = 1.5,
            LineStyle = LineStyle.Dash
        };

        line.Points.Add(new DataPoint(x, 0));
        line.Points.Add(new DataPoint(x, 1));
        model.Series.Add(line);
    }
}
