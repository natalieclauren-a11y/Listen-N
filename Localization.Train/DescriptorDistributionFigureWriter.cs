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

internal static class DescriptorDistributionFigureWriter
{
    public static void Write(
        string outputPath,
        IReadOnlyList<double> entropySingle, IReadOnlyList<double> entropyDual,
        IReadOnlyList<double> giniSingle, IReadOnlyList<double> giniDual,
        IReadOnlyList<double> anisSingle, IReadOnlyList<double> anisDual,
        IReadOnlyList<double> dipoleSingle, IReadOnlyList<double> dipoleDual,
        int bins,
        string title,
        string subtitle)
    {
        var entropyModel = BuildHistogramModel("Entropy", title, subtitle, entropySingle, entropyDual, bins);
        var giniModel = BuildHistogramModel("Gini", title, subtitle, giniSingle, giniDual, bins);
        var anisModel = BuildHistogramModel("Anisotropy", title, subtitle, anisSingle, anisDual, bins);
        var dipoleModel = BuildHistogramModel("Dipole magnitude", title, subtitle, dipoleSingle, dipoleDual, bins);

        byte[] entropyPng = ExportToPng(entropyModel, 900, 600);
        byte[] giniPng = ExportToPng(giniModel, 900, 600);
        byte[] anisPng = ExportToPng(anisModel, 900, 600);
        byte[] dipolePng = ExportToPng(dipoleModel, 900, 600);

        byte[] stitched = Stitch(entropyPng, giniPng, anisPng, dipolePng);

        string directory = Path.GetDirectoryName(outputPath) ?? ".";
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(outputPath, stitched);
    }

    private static PlotModel BuildHistogramModel(string descriptorTitle, string title, string subtitle, IReadOnlyList<double> singleValues, IReadOnlyList<double> dualValues, int bins)
    {
        var model = new PlotModel
        {
            Title = descriptorTitle,
            Subtitle = $"{title}\n{subtitle}"
        };

        var xAxis = new LinearAxis
        {
            Position = AxisPosition.Bottom,
            Title = descriptorTitle,
            MajorGridlineStyle = LineStyle.Dash,
            MinorGridlineStyle = LineStyle.Dot
        };

        var yAxis = new LinearAxis
        {
            Position = AxisPosition.Left,
            Title = "Count",
            Minimum = 0,
            MajorGridlineStyle = LineStyle.Dash,
            MinorGridlineStyle = LineStyle.Dot
        };

        model.Axes.Add(xAxis);
        model.Axes.Add(yAxis);

        var combined = singleValues.Concat(dualValues).ToList();
        double min = combined.Count == 0 ? 0 : combined.Min();
        double max = combined.Count == 0 ? 1 : combined.Max();
        if (Math.Abs(max - min) < 1e-12)
        {
            max = min + 1;
        }

        double binWidth = (max - min) / bins;
        var binCenters = Enumerable.Range(0, bins).Select(i => min + (i + 0.5) * binWidth).ToArray();

        var singleCounts = CountBins(singleValues, min, max, bins);
        var dualCounts = CountBins(dualValues, min, max, bins);

        model.Series.Add(BuildStemSeries("Single", binCenters, singleCounts));
        model.Series.Add(BuildStemSeries("Dual", binCenters, dualCounts));

        return model;
    }

    private static StemSeries BuildStemSeries(string title, double[] centers, int[] counts)
    {
        var series = new StemSeries
        {
            Title = title,
            StrokeThickness = 2,
            MarkerType = MarkerType.Circle,
            MarkerSize = 3
        };

        for (int i = 0; i < centers.Length; i++)
        {
            series.Points.Add(new DataPoint(centers[i], counts[i]));
        }

        return series;
    }

    private static int[] CountBins(IReadOnlyList<double> values, double min, double max, int bins)
    {
        var counts = new int[bins];
        if (values.Count == 0)
        {
            return counts;
        }

        double range = max - min;
        foreach (var value in values)
        {
            int index = (int)((value - min) / range * bins);
            if (index >= bins)
            {
                index = bins - 1;
            }

            index = Math.Max(0, Math.Min(bins - 1, index));
            counts[index]++;
        }

        return counts;
    }

    private static byte[] ExportToPng(PlotModel model, int width, int height)
    {
        using var stream = new MemoryStream();
        var exporter = new PngExporter { Width = width, Height = height };
        exporter.Export(model, stream);
        return stream.ToArray();
    }

    private static byte[] Stitch(byte[] topLeft, byte[] topRight, byte[] bottomLeft, byte[] bottomRight)
    {
        using var tl = SKBitmap.Decode(topLeft);
        using var tr = SKBitmap.Decode(topRight);
        using var bl = SKBitmap.Decode(bottomLeft);
        using var br = SKBitmap.Decode(bottomRight);

        int width = tl.Width;
        int height = tl.Height;

        using var combined = new SKBitmap(width * 2, height * 2);
        using var canvas = new SKCanvas(combined);
        canvas.DrawBitmap(tl, 0, 0);
        canvas.DrawBitmap(tr, width, 0);
        canvas.DrawBitmap(bl, 0, height);
        canvas.DrawBitmap(br, width, height);

        using var image = SKImage.FromBitmap(combined);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}
