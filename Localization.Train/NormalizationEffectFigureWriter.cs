using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using Localization.ML;
using OxyPlot;
using OxyPlot.Axes;
using OxyPlot.Series;
using OxyPlot.SkiaSharp;
using SkiaSharp;

namespace Localization.Train;

internal static class NormalizationEffectFigureWriter
{
    public static void Write(string outputPath, LocalizationRow low, LocalizationRow high, string title, string subtitle)
    {
        if (low == null)
        {
            throw new ArgumentNullException(nameof(low));
        }

        if (high == null)
        {
            throw new ArgumentNullException(nameof(high));
        }

        var rawModel = BuildRawPlotModel(low, high, title, subtitle);
        var normalizedModel = BuildNormalizedPlotModel(low, high, title, subtitle);

        byte[] rawPng = ExportToPngBytes(rawModel, 900, 600);
        byte[] normalizedPng = ExportToPngBytes(normalizedModel, 900, 600);

        byte[] stitched = StitchHorizontalPng(rawPng, normalizedPng);

        string directory = Path.GetDirectoryName(outputPath) ?? ".";
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(outputPath, stitched);
    }

    private static PlotModel BuildRawPlotModel(LocalizationRow low, LocalizationRow high, string title, string subtitle)
    {
        var model = new PlotModel
        {
            Title = title,
            Subtitle = subtitle
        };

        var categoryAxis = new CategoryAxis
        {
            Position = AxisPosition.Bottom,
            Title = "Tube",
            GapWidth = 0
        };

        for (int i = 1; i <= FeatureBuilder.ChannelCount; i++)
        {
            categoryAxis.Labels.Add(i.ToString(CultureInfo.InvariantCulture));
        }

        model.Axes.Add(categoryAxis);
        model.Axes.Add(new LinearAxis { Position = AxisPosition.Left, Title = "Counts in window", Minimum = 0 });

        model.Series.Add(BuildSeries(low, "Low rate", row => row.Channels));
        model.Series.Add(BuildSeries(high, "High rate", row => row.Channels));

        return model;
    }

    private static PlotModel BuildNormalizedPlotModel(LocalizationRow low, LocalizationRow high, string title, string subtitle)
    {
        var model = new PlotModel
        {
            Title = title,
            Subtitle = subtitle
        };

        var categoryAxis = new CategoryAxis
        {
            Position = AxisPosition.Bottom,
            Title = "Tube",
            GapWidth = 0
        };

        for (int i = 1; i <= FeatureBuilder.ChannelCount; i++)
        {
            categoryAxis.Labels.Add(i.ToString(CultureInfo.InvariantCulture));
        }

        model.Axes.Add(categoryAxis);
        model.Axes.Add(new LinearAxis { Position = AxisPosition.Left, Title = "Fraction of total counts", Minimum = 0, Maximum = 1 });

        model.Series.Add(BuildSeries(low, "Low rate", row => Normalize(row.Channels)));
        model.Series.Add(BuildSeries(high, "High rate", row => Normalize(row.Channels)));

        return model;
    }

    private static LineSeries BuildSeries(LocalizationRow row, string labelPrefix, Func<LocalizationRow, IReadOnlyList<double>> selector)
    {
        double total = row.Channels.Sum();
        var series = new LineSeries
        {
            Title = $"{labelPrefix} (Total={total:F0})",
            MarkerType = MarkerType.Circle,
            StrokeThickness = 2,
            MarkerSize = 4
        };

        var values = selector(row);
        for (int i = 0; i < values.Count; i++)
        {
            series.Points.Add(new DataPoint(i, values[i]));
        }

        return series;
    }

    private static IReadOnlyList<double> Normalize(IReadOnlyList<double> channels)
    {
        double total = channels.Sum();
        if (total <= 0)
        {
            return Enumerable.Repeat(0d, channels.Count).ToArray();
        }

        var normalized = new double[channels.Count];
        for (int i = 0; i < channels.Count; i++)
        {
            normalized[i] = channels[i] / total;
        }

        return normalized;
    }

    private static byte[] ExportToPngBytes(PlotModel model, int width, int height)
    {
        using var stream = new MemoryStream();
        var exporter = new PngExporter { Width = width, Height = height };
        exporter.Export(model, stream);
        return stream.ToArray();
    }

    private static byte[] StitchHorizontalPng(byte[] left, byte[] right)
    {
        using var leftBitmap = SKBitmap.Decode(left);
        using var rightBitmap = SKBitmap.Decode(right);

        int combinedWidth = leftBitmap.Width + rightBitmap.Width;
        int combinedHeight = Math.Max(leftBitmap.Height, rightBitmap.Height);

        using var combined = new SKBitmap(combinedWidth, combinedHeight);
        using var canvas = new SKCanvas(combined);
        canvas.DrawBitmap(leftBitmap, 0, 0);
        canvas.DrawBitmap(rightBitmap, leftBitmap.Width, 0);

        using var image = SKImage.FromBitmap(combined);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}
