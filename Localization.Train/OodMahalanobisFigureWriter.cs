using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OxyPlot;
using OxyPlot.Axes;
using OxyPlot.Series;
using OxyPlot.SkiaSharp;
using SkiaSharp;

namespace Localization.Train;

internal static class OodMahalanobisFigureWriter
{
    public static void Write(
        string outputPath,
        IReadOnlyList<double> trainDistances,
        IReadOnlyList<double> evalDistances,
        IReadOnlyList<(double Distance, double ErrorCm)> evalPoints,
        double threshold,
        int bins,
        string title,
        string subtitle)
    {
        var histogram = BuildHistogramPlotModel(trainDistances, evalDistances, threshold, bins, title);
        var scatter = BuildScatterPlotModel(evalPoints, threshold, bins, subtitle);

        using var histStream = new MemoryStream();
        using var scatterStream = new MemoryStream();
        new PngExporter { Width = 900, Height = 600 }.Export(histogram, histStream);
        new PngExporter { Width = 900, Height = 600 }.Export(scatter, scatterStream);

        histStream.Position = 0;
        scatterStream.Position = 0;
        using var histBitmap = SKBitmap.Decode(histStream);
        using var scatterBitmap = SKBitmap.Decode(scatterStream);

        int height = Math.Max(histBitmap.Height, scatterBitmap.Height);
        int width = histBitmap.Width + scatterBitmap.Width;
        using var combined = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(combined))
        {
            canvas.Clear(SKColors.White);
            canvas.DrawBitmap(histBitmap, new SKPoint(0, 0));
            canvas.DrawBitmap(scatterBitmap, new SKPoint(histBitmap.Width, 0));
        }

        using var image = SKImage.FromBitmap(combined);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        using var file = File.Open(outputPath, FileMode.Create, FileAccess.Write);
        data.SaveTo(file);
    }

    private static PlotModel BuildHistogramPlotModel(
        IReadOnlyList<double> trainDistances,
        IReadOnlyList<double> evalDistances,
        double threshold,
        int bins,
        string title)
    {
        double min = Math.Min(trainDistances.DefaultIfEmpty(0).Min(), evalDistances.DefaultIfEmpty(0).Min());
        double max = Math.Max(trainDistances.DefaultIfEmpty(0).Max(), evalDistances.DefaultIfEmpty(0).Max());
        if (Math.Abs(max - min) < 1e-6)
        {
            max = min + 1;
        }

        var model = new PlotModel { Title = title };
        model.Axes.Add(new LinearAxis { Position = AxisPosition.Bottom, Title = "Mahalanobis distance (4D descriptor space)", Minimum = min, Maximum = max });
        model.Axes.Add(new LinearAxis { Position = AxisPosition.Left, Title = "Count", Minimum = 0 });

        var trainHist = ComputeHistogram(trainDistances, min, max, bins);
        var evalHist = ComputeHistogram(evalDistances, min, max, bins);

        double yMax = Math.Max(trainHist.DefaultIfEmpty((0, 0)).Max(h => h.Count),
            evalHist.DefaultIfEmpty((0, 0)).Max(h => h.Count));
        if (yMax <= 0)
        {
            yMax = 1;
        }

        model.Series.Add(BuildHistogramArea(trainHist, "Train", OxyColor.FromAColor(90, OxyColors.SteelBlue), OxyColors.SteelBlue));
        model.Series.Add(BuildHistogramArea(evalHist, "Eval", OxyColor.FromAColor(90, OxyColors.IndianRed), OxyColors.IndianRed));

        model.Series.Add(BuildVerticalLine(threshold, yMax, "Threshold", OxyColors.DarkRed));

        return model;
    }

    private static PlotModel BuildScatterPlotModel(
        IReadOnlyList<(double Distance, double ErrorCm)> evalPoints,
        double threshold,
        int bins,
        string subtitle)
    {
        double minX = evalPoints.Count == 0 ? 0 : evalPoints.Min(p => p.Distance);
        double maxX = Math.Max(threshold, evalPoints.Count == 0 ? 1 : evalPoints.Max(p => p.Distance));
        if (Math.Abs(maxX - minX) < 1e-6)
        {
            maxX = minX + 1;
        }

        var model = new PlotModel { Title = "Localization error vs Mahalanobis distance", Subtitle = subtitle };
        model.Axes.Add(new LinearAxis { Position = AxisPosition.Bottom, Title = "Mahalanobis distance", Minimum = minX, Maximum = maxX });
        model.Axes.Add(new LinearAxis { Position = AxisPosition.Left, Title = "Localization error (cm)", Minimum = 0 });

        var scatter = new ScatterSeries
        {
            MarkerType = MarkerType.Circle,
            MarkerFill = OxyColor.FromAColor(120, OxyColors.SteelBlue),
            MarkerSize = 2.5,
            Title = "Eval"
        };

        foreach (var point in evalPoints)
        {
            scatter.Points.Add(new ScatterPoint(point.Distance, point.ErrorCm));
        }

        model.Series.Add(scatter);

        var curve = BuildMedianCurve(evalPoints, bins, minX, maxX);
        if (curve.Points.Count > 0)
        {
            model.Series.Add(curve);
        }

        double yMax = evalPoints.Count == 0 ? 1 : Math.Max(1, evalPoints.Max(p => p.ErrorCm));
        model.Series.Add(BuildVerticalLine(threshold, yMax, "Threshold", OxyColors.DarkRed));

        return model;
    }

    private static IReadOnlyList<(double X, double Count)> ComputeHistogram(IReadOnlyList<double> values, double min, double max, int bins)
    {
        var counts = new double[bins];
        if (bins <= 0) return Array.Empty<(double, double)>();
        double width = (max - min) / bins;
        if (width <= 0) width = 1;

        foreach (var v in values)
        {
            int idx = (int)Math.Floor((v - min) / width);
            if (idx < 0) idx = 0;
            if (idx >= bins) idx = bins - 1;
            counts[idx] += 1;
        }

        var result = new List<(double X, double Count)>(bins);
        for (int i = 0; i < bins; i++)
        {
            double center = min + (i + 0.5) * width;
            result.Add((center, counts[i]));
        }
        return result;
    }

    private static AreaSeries BuildHistogramArea(IReadOnlyList<(double X, double Count)> hist, string title, OxyColor fill, OxyColor stroke)
    {
        var area = new AreaSeries
        {
            Title = title,
            Color = stroke,
            Fill = fill,
            StrokeThickness = 1.5
        };

        foreach (var (x, c) in hist)
        {
            area.Points.Add(new DataPoint(x, c));
        }
        for (int i = hist.Count - 1; i >= 0; i--)
        {
            area.Points2.Add(new DataPoint(hist[i].X, 0));
        }

        return area;
    }

    private static LineSeries BuildVerticalLine(double x, double yMax, string title, OxyColor color)
    {
        var line = new LineSeries
        {
            Title = title,
            Color = color,
            StrokeThickness = 2
        };
        line.Points.Add(new DataPoint(x, 0));
        line.Points.Add(new DataPoint(x, yMax));
        return line;
    }

    private static LineSeries BuildMedianCurve(
        IReadOnlyList<(double Distance, double ErrorCm)> evalPoints,
        int bins,
        double minX,
        double maxX)
    {
        var series = new LineSeries
        {
            Title = "Median error",
            Color = OxyColors.DarkOrange,
            StrokeThickness = 2
        };

        if (evalPoints.Count == 0)
        {
            return series;
        }

        int curveBins = Math.Max(5, Math.Min(60, bins));
        double width = (maxX - minX) / curveBins;
        if (width <= 0)
        {
            return series;
        }

        for (int i = 0; i < curveBins; i++)
        {
            double lower = minX + i * width;
            double upper = lower + width;
            var bucket = evalPoints.Where(p => p.Distance >= lower && p.Distance < upper).Select(p => p.ErrorCm).OrderBy(v => v).ToList();
            if (bucket.Count == 0)
            {
                continue;
            }

            double median = bucket[bucket.Count / 2];
            double center = (lower + upper) / 2;
            series.Points.Add(new DataPoint(center, median));
        }

        return series;
    }
}
