using System.IO;
using OxyPlot;
using OxyPlot.Axes;
using OxyPlot.Series;
using OxyPlot.SkiaSharp;

namespace Localization.Train;

public static class OutcomeDistributionFigureWriter
{
    public static void Write(
        string outputPath,
        double singleFrac,
        double dualFrac,
        double centroidFrac,
        double refuseFrac,
        string title,
        string subtitle)
    {
        var model = new PlotModel
        {
            Title = title,
            Subtitle = subtitle + " | 1=Single 2=Dual 3=Centroid 4=Refuse",
        };

        var xAxis = new LinearAxis
        {
            Position = AxisPosition.Bottom,
            Minimum = 0.5,
            Maximum = 4.5,
            MajorStep = 1,
            MajorGridlineStyle = LineStyle.Solid,
            MinorGridlineStyle = LineStyle.None,
            MajorTickSize = 0,
            MinorTickSize = 0,
            LabelFormatter = _ => string.Empty,
        };

        model.Axes.Add(xAxis);

        var valueAxis = new LinearAxis
        {
            Position = AxisPosition.Left,
            Minimum = 0,
            Maximum = 1,
            MajorGridlineStyle = LineStyle.Solid,
            MinorGridlineStyle = LineStyle.None,
            Title = "Fraction of windows",
        };

        model.Axes.Add(valueAxis);

        model.Series.Add(Bar(1, singleFrac, 0.8, "Single", OxyColor.FromAColor(140, OxyColors.SteelBlue), OxyColors.SteelBlue));
        model.Series.Add(Bar(2, dualFrac, 0.8, "Dual", OxyColor.FromAColor(140, OxyColors.ForestGreen), OxyColors.ForestGreen));
        model.Series.Add(Bar(3, centroidFrac, 0.8, "Centroid", OxyColor.FromAColor(140, OxyColors.Goldenrod), OxyColors.Goldenrod));
        model.Series.Add(Bar(4, refuseFrac, 0.8, "Refuse", OxyColor.FromAColor(140, OxyColors.IndianRed), OxyColors.IndianRed));

        using var stream = File.Create(outputPath);
        var pngExporter = new PngExporter { Width = 900, Height = 600 };
        pngExporter.Export(model, stream);
    }

    private static AreaSeries Bar(double xCenter, double height, double width, string title, OxyColor fill, OxyColor stroke)
    {
        double x0 = xCenter - width / 2.0;
        double x1 = xCenter + width / 2.0;

        var series = new AreaSeries
        {
            Title = title,
            Color = stroke,
            Fill = fill,
            StrokeThickness = 1.5,
        };

        series.Points.Add(new DataPoint(x0, height));
        series.Points.Add(new DataPoint(x1, height));

        series.Points2.Add(new DataPoint(x0, 0));
        series.Points2.Add(new DataPoint(x1, 0));

        return series;
    }
}
