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
        var model = new PlotModel { Title = title };

        model.Subtitle = subtitle;

        var categoryAxis = new CategoryAxis
        {
            Position = AxisPosition.Bottom,
            ItemsSource = new[] { "Single", "Dual", "Centroid", "Refuse" },
            MajorGridlineStyle = LineStyle.Solid,
            MinorGridlineStyle = LineStyle.None,
            GapWidth = 0.2
        };

        model.Axes.Add(categoryAxis);

        var valueAxis = new LinearAxis
        {
            Position = AxisPosition.Left,
            Minimum = 0,
            Maximum = 1,
            MajorGridlineStyle = LineStyle.Solid,
            MinorGridlineStyle = LineStyle.None,
            Title = "Fraction of windows"
        };

        model.Axes.Add(valueAxis);

        var series = new ColumnSeries
        {
            StrokeThickness = 1,
            FillColor = OxyColors.SteelBlue,
            StrokeColor = OxyColors.Black,
            ColumnWidth = 0.5
        };

        series.Items.Add(new ColumnItem(singleFrac));
        series.Items.Add(new ColumnItem(dualFrac));
        series.Items.Add(new ColumnItem(centroidFrac));
        series.Items.Add(new ColumnItem(refuseFrac));

        model.Series.Add(series);

        var pngExporter = new PngExporter { Width = 900, Height = 600, Background = OxyColors.White };
        pngExporter.ExportToFile(model, outputPath);
    }
}
