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

internal static class ClassifierPerformanceFigureWriter
{
    public static void Write(
        string outputPath,
        IReadOnlyList<(double Fpr, double Tpr)> roc,
        IReadOnlyList<(double Recall, double Precision)> pr,
        (double Fpr, double Tpr) rocOp,
        (double Recall, double Precision) prOp,
        double auc,
        double ap,
        double threshold,
        string title,
        string subtitle)
    {
        var rocModel = BuildRocPlot(roc, rocOp, auc, threshold, title, subtitle);
        var prModel = BuildPrPlot(pr, prOp, ap, threshold, title, subtitle);

        using var rocStream = new MemoryStream();
        using var prStream = new MemoryStream();
        new PngExporter { Width = 900, Height = 600 }.Export(rocModel, rocStream);
        new PngExporter { Width = 900, Height = 600 }.Export(prModel, prStream);

        rocStream.Position = 0;
        prStream.Position = 0;

        using var rocBitmap = SKBitmap.Decode(rocStream);
        using var prBitmap = SKBitmap.Decode(prStream);

        int height = Math.Max(rocBitmap.Height, prBitmap.Height);
        int width = rocBitmap.Width + prBitmap.Width;

        using var combined = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(combined))
        {
            canvas.Clear(SKColors.White);
            canvas.DrawBitmap(rocBitmap, new SKPoint(0, 0));
            canvas.DrawBitmap(prBitmap, new SKPoint(rocBitmap.Width, 0));
        }

        using var image = SKImage.FromBitmap(combined);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        using var file = File.Open(outputPath, FileMode.Create, FileAccess.Write);
        data.SaveTo(file);
    }

    private static PlotModel BuildRocPlot(
        IReadOnlyList<(double Fpr, double Tpr)> points,
        (double Fpr, double Tpr) op,
        double auc,
        double threshold,
        string title,
        string subtitle)
    {
        var model = new PlotModel
        {
            Title = "ROC",
            Subtitle = $"{title} | AUC={auc:F3}{(string.IsNullOrWhiteSpace(subtitle) ? string.Empty : $" | {subtitle}")}",
        };

        model.Axes.Add(new LinearAxis { Position = AxisPosition.Bottom, Title = "False positive rate", Minimum = 0, Maximum = 1 });
        model.Axes.Add(new LinearAxis { Position = AxisPosition.Left, Title = "True positive rate", Minimum = 0, Maximum = 1 });

        var diag = new LineSeries
        {
            Color = OxyColors.Gray,
            LineStyle = LineStyle.Dash,
            StrokeThickness = 1.2,
            Title = "y=x"
        };
        diag.Points.Add(new DataPoint(0, 0));
        diag.Points.Add(new DataPoint(1, 1));
        model.Series.Add(diag);

        var series = new LineSeries { Color = OxyColors.SteelBlue, StrokeThickness = 2 };
        foreach (var p in points.OrderBy(p => p.Fpr))
        {
            series.Points.Add(new DataPoint(p.Fpr, p.Tpr));
        }

        var opSeries = new ScatterSeries
        {
            MarkerType = MarkerType.Diamond,
            MarkerFill = OxyColors.DarkOrange,
            MarkerStroke = OxyColors.Brown,
            MarkerStrokeThickness = 1.5,
            MarkerSize = 6,
            Title = $"Operating point (t={threshold:F2})"
        };
        opSeries.Points.Add(new ScatterPoint(op.Fpr, op.Tpr));

        model.Series.Add(series);
        model.Series.Add(opSeries);
        return model;
    }

    private static PlotModel BuildPrPlot(
        IReadOnlyList<(double Recall, double Precision)> points,
        (double Recall, double Precision) op,
        double ap,
        double threshold,
        string title,
        string subtitle)
    {
        var model = new PlotModel
        {
            Title = "Precision–Recall",
            Subtitle = $"{title} | AP={ap:F3}{(string.IsNullOrWhiteSpace(subtitle) ? string.Empty : $" | {subtitle}")}",
        };

        model.Axes.Add(new LinearAxis { Position = AxisPosition.Bottom, Title = "Recall", Minimum = 0, Maximum = 1 });
        model.Axes.Add(new LinearAxis { Position = AxisPosition.Left, Title = "Precision", Minimum = 0, Maximum = 1 });

        var series = new LineSeries { Color = OxyColors.SeaGreen, StrokeThickness = 2 };
        foreach (var p in points.OrderBy(p => p.Recall))
        {
            series.Points.Add(new DataPoint(p.Recall, p.Precision));
        }

        var opSeries = new ScatterSeries
        {
            MarkerType = MarkerType.Diamond,
            MarkerFill = OxyColors.DarkOrange,
            MarkerStroke = OxyColors.Brown,
            MarkerStrokeThickness = 1.5,
            MarkerSize = 6,
            Title = $"Operating point (t={threshold:F2})"
        };
        opSeries.Points.Add(new ScatterPoint(op.Recall, op.Precision));

        model.Series.Add(series);
        model.Series.Add(opSeries);
        return model;
    }
}
