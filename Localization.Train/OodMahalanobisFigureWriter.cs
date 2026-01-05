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
        new PngExporter { Width = 900, Height = 600, Background = OxyColors.White }.Export(histogram, histStream);
        new PngExporter { Width = 900, Height = 600, Background = OxyColors.White }.Export(scatter, scatterStream);

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

        double width = (max - min) / bins;
        var trainSeries = new HistogramSeries
        {
            Title = "Train",
            StrokeColor = OxyColors.SteelBlue,
            FillColor = OxyColor.FromAColor(120, OxyColors.SteelBlue),
            StrokeThickness = 1.5
        };
        trainSeries.Items.AddRange(HistogramHelpers.CreateHistogram(trainDistances, min, max, width).Items);

        var evalSeries = new HistogramSeries
        {
            Title = "Eval",
            StrokeColor = OxyColors.IndianRed,
            FillColor = OxyColor.FromAColor(110, OxyColors.IndianRed),
            StrokeThickness = 1.5
        };
        evalSeries.Items.AddRange(HistogramHelpers.CreateHistogram(evalDistances, min, max, width).Items);

        model.Series.Add(trainSeries);
        model.Series.Add(evalSeries);

        model.LegendPlacement = LegendPlacement.Outside;
        model.LegendPosition = LegendPosition.BottomCenter;

        model.Annotations.Add(new LineAnnotation
        {
            Type = LineAnnotationType.Vertical,
            X = threshold,
            Color = OxyColors.DarkRed,
            Text = "Threshold",
            TextColor = OxyColors.DarkRed,
            StrokeThickness = 2
        });

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

        model.Annotations.Add(new LineAnnotation
        {
            Type = LineAnnotationType.Vertical,
            X = threshold,
            Color = OxyColors.DarkRed,
            Text = "Threshold",
            TextColor = OxyColors.DarkRed,
            StrokeThickness = 2
        });

        return model;
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
