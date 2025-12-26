using System;
using System.Linq;
using Localization.ML;
using Xunit;

namespace Localization.ML.Tests;

public class CalibrationModelTests
{
    [Fact]
    public void IsotonicCalibrationIsMonotoneAndBounded()
    {
        var samples = Enumerable.Range(0, 20)
            .Select(i =>
            {
                double p = 0.05 + 0.9 * i / 19.0;
                bool label = p > 0.5;
                return (Probability: p, Label: label);
            })
            .ToList();

        var model = CalibrationModel.FitIsotonic(samples);
        double previous = 0;
        foreach (var p in Enumerable.Range(0, 10).Select(i => i / 9.0))
        {
            double calibrated = model.Apply(p);
            Assert.False(double.IsNaN(calibrated));
            Assert.InRange(calibrated, 0, 1);
            Assert.True(calibrated + 1e-6 >= previous);
            previous = calibrated;
        }
    }

    [Fact]
    public void PlattCalibrationStaysFinite()
    {
        var rnd = new Random(123);
        var samples = Enumerable.Range(0, 50)
            .Select(_ =>
            {
                double p = rnd.NextDouble();
                bool label = rnd.NextDouble() > 0.5;
                return (Probability: p, Label: label);
            })
            .ToList();

        var model = CalibrationModel.FitPlatt(samples);
        foreach (var sample in samples.Take(10))
        {
            double calibrated = model.Apply(sample.Probability);
            Assert.False(double.IsNaN(calibrated));
            Assert.InRange(calibrated, 0, 1);
        }
    }
}
