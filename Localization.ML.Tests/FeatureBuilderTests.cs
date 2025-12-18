using System;
using System.Linq;
using Localization.ML;
using Xunit;

namespace Localization.ML.Tests;

public class FeatureBuilderTests
{
    [Fact]
    public void UniformCountsProduceExpectedDescriptors()
    {
        var builder = new FeatureBuilder(epsilon: 1e-9);
        var counts = Enumerable.Repeat(1.0, FeatureBuilder.ChannelCount).ToArray();

        var features = builder.BuildFeatures(counts, durationSeconds: 30);

        Assert.Equal(15, features.TotalCounts);
        Assert.All(features.Normalized, p => Assert.Equal(1.0 / 15.0, p, 6));
        Assert.InRange(features.Entropy, Math.Log(15) - 1e-6, Math.Log(15) + 1e-6);
        Assert.InRange(features.Gini, 0.933, 0.934);
        Assert.Equal(0, features.Anisotropy, 6);
        Assert.Equal(7.0, features.DipoleMagnitude, 6);
    }

    [Fact]
    public void HandlesZeroCountsGracefully()
    {
        var builder = new FeatureBuilder();
        var counts = new double[FeatureBuilder.ChannelCount];

        var features = builder.BuildFeatures(counts);

        Assert.Equal(0, features.TotalCounts);
        Assert.All(features.Normalized, p => Assert.Equal(0, p));
        Assert.Equal(0, features.Entropy);
        Assert.Equal(1, features.Gini);
        Assert.Equal(0, features.Anisotropy);
        Assert.Equal(0, features.DipoleMagnitude);
        Assert.True(double.IsNaN(features.FeatureVector.Last()));
    }

    [Fact]
    public void CustomDipolePositionsAreHonored()
    {
        var positions = Enumerable.Range(1, FeatureBuilder.ChannelCount).Select(i => (double)i).ToArray();
        var builder = new FeatureBuilder(dipolePositions: positions);
        var counts = new double[FeatureBuilder.ChannelCount];
        counts[0] = 10;
        counts[14] = 20;

        var features = builder.BuildFeatures(counts);
        double total = 30.0;
        double expected = Math.Abs((counts[0] / total) * 1 + (counts[14] / total) * 15);

        Assert.Equal(expected, features.DipoleMagnitude, 6);
    }
}
