using System;
using System.Collections.Generic;
using System.Linq;

namespace Localization.ML;

public sealed class FeatureBuilder
{
    public const int ChannelCount = 15;
    public IReadOnlyList<string> FeatureNames { get; }
    public IReadOnlyList<double> DipolePositions { get; }
    public double Epsilon { get; }

    public FeatureBuilder(double epsilon = 1e-9, IReadOnlyList<double>? dipolePositions = null)
    {
        DipolePositions = dipolePositions ?? Enumerable.Range(0, ChannelCount).Select(i => (double)i).ToArray();
        if (DipolePositions.Count != ChannelCount)
        {
            throw new ArgumentException($"Dipole position vector must have {ChannelCount} entries", nameof(dipolePositions));
        }

        Epsilon = epsilon;
        FeatureNames = BuildFeatureNames();
    }

    private static IReadOnlyList<string> BuildFeatureNames()
    {
        var names = new List<string>();
        for (int i = 1; i <= ChannelCount; i++)
        {
            names.Add($"Channel{i}");
        }

        names.Add("TotalCounts");

        for (int i = 1; i <= ChannelCount; i++)
        {
            names.Add($"Channel{i}_norm");
        }

        names.Add("Entropy");
        names.Add("Gini");
        names.Add("Anisotropy");
        names.Add("DipoleMagnitude");
        names.Add("Duration_s");
        return names;
    }

    public FeatureComputationResult BuildFeatures(IReadOnlyList<double> channels, double? durationSeconds = null)
    {
        if (channels == null)
        {
            throw new ArgumentNullException(nameof(channels));
        }

        if (channels.Count != ChannelCount)
        {
            throw new ArgumentException($"Expected {ChannelCount} channels but received {channels.Count}", nameof(channels));
        }

        if (channels.Any(double.IsNaN) || channels.Any(double.IsInfinity))
        {
            throw new ArgumentException("Channel inputs must be finite numbers", nameof(channels));
        }

        double total = channels.Sum();
        double[] normalized = new double[ChannelCount];
        if (total > 0)
        {
            for (int i = 0; i < ChannelCount; i++)
            {
                normalized[i] = channels[i] / total;
            }
        }

        double entropy = ComputeEntropy(normalized);
        double gini = ComputeGini(normalized);
        double anisotropy = ComputeAnisotropy(normalized);
        double dipole = ComputeDipole(normalized, DipolePositions);

        double duration = durationSeconds ?? double.NaN;

        var featureVector = new List<double>(FeatureNames.Count);
        featureVector.AddRange(channels);
        featureVector.Add(total);
        featureVector.AddRange(normalized);
        featureVector.Add(entropy);
        featureVector.Add(gini);
        featureVector.Add(anisotropy);
        featureVector.Add(dipole);
        featureVector.Add(duration);

        return new FeatureComputationResult
        {
            Channels = channels.ToArray(),
            TotalCounts = total,
            Normalized = normalized,
            Entropy = entropy,
            Gini = gini,
            Anisotropy = anisotropy,
            DipoleMagnitude = dipole,
            DurationSeconds = durationSeconds,
            FeatureVector = featureVector.ToArray()
        };
    }

    public void EnsureFeatureParity(double[] features)
    {
        if (features.Length != FeatureNames.Count)
        {
            throw new InvalidOperationException($"Feature vector length {features.Length} does not match expected {FeatureNames.Count}");
        }

        if (features.Any(double.IsNaN) || features.Any(double.IsInfinity))
        {
            throw new InvalidOperationException("Feature vector contains NaN or Infinity");
        }
    }

    private double ComputeEntropy(IReadOnlyList<double> normalized)
    {
        double entropy = 0d;
        for (int i = 0; i < normalized.Count; i++)
        {
            double p = normalized[i];
            if (p <= 0)
            {
                continue;
            }

            entropy -= p * Math.Log(p + Epsilon);
        }

        return entropy;
    }

    private static double ComputeGini(IReadOnlyList<double> normalized)
    {
        double sumSquares = normalized.Sum(p => p * p);
        return 1d - sumSquares;
    }

    private static double ComputeAnisotropy(IReadOnlyList<double> normalized)
    {
        double mean = normalized.Sum() / normalized.Count;
        double variance = normalized.Sum(p => Math.Pow(p - mean, 2)) / normalized.Count;
        return variance;
    }

    private static double ComputeDipole(IReadOnlyList<double> normalized, IReadOnlyList<double> positions)
    {
        double weighted = 0d;
        for (int i = 0; i < normalized.Count; i++)
        {
            weighted += normalized[i] * positions[i];
        }

        return Math.Abs(weighted);
    }
}

public sealed record FeatureComputationResult
{
    public required double[] Channels { get; init; }
    public required double[] Normalized { get; init; }
    public required double TotalCounts { get; init; }
    public required double Entropy { get; init; }
    public required double Gini { get; init; }
    public required double Anisotropy { get; init; }
    public required double DipoleMagnitude { get; init; }
    public double? DurationSeconds { get; init; }
    public required double[] FeatureVector { get; init; }
}
