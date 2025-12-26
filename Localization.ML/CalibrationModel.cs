using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace Localization.ML;

public enum CalibrationKind
{
    None,
    Platt,
    Isotonic
}

public sealed class CalibrationModel
{
    [JsonInclude]
    public CalibrationKind Kind { get; private set; }

    [JsonInclude]
    public double Slope { get; private set; }

    [JsonInclude]
    public double Intercept { get; private set; }

    [JsonInclude]
    public IReadOnlyList<double> IsotonicX { get; private set; } = Array.Empty<double>();

    [JsonInclude]
    public IReadOnlyList<double> IsotonicY { get; private set; } = Array.Empty<double>();

    [JsonInclude]
    public int ReliabilityBinCount { get; private set; }

    [JsonConstructor]
    public CalibrationModel()
    {
        Kind = CalibrationKind.None;
    }

    private CalibrationModel(CalibrationKind kind, double slope, double intercept, IReadOnlyList<double> isotonicX, IReadOnlyList<double> isotonicY, int reliabilityBins)
    {
        Kind = kind;
        Slope = slope;
        Intercept = intercept;
        IsotonicX = isotonicX;
        IsotonicY = isotonicY;
        ReliabilityBinCount = reliabilityBins;
    }

    public static CalibrationModel Identity(int reliabilityBins = 10)
    {
        return new CalibrationModel(CalibrationKind.None, 1, 0, Array.Empty<double>(), Array.Empty<double>(), reliabilityBins);
    }

    public static CalibrationModel FitPlatt(IEnumerable<(double Probability, bool Label)> samples, int reliabilityBins = 10, int maxIterations = 100, double tolerance = 1e-6)
    {
        var data = samples.Select(s => (X: Logit(ClampProbability(s.Probability)), Y: s.Label ? 1.0 : 0.0)).ToList();
        if (data.Count == 0)
        {
            return Identity(reliabilityBins);
        }

        double a = 1.0;
        double b = 0.0;

        for (int iter = 0; iter < maxIterations; iter++)
        {
            double g1 = 0, g2 = 0;
            double h11 = 0, h22 = 0, h12 = 0;

            foreach (var (x, y) in data)
            {
                double z = a * x + b;
                double p = 1.0 / (1.0 + Math.Exp(-z));
                double w = p * (1 - p);
                double diff = p - y;

                g1 += diff * x;
                g2 += diff;

                h11 += w * x * x;
                h22 += w;
                h12 += w * x;
            }

            double det = (h11 * h22) - (h12 * h12);
            if (Math.Abs(det) < 1e-12)
            {
                break;
            }

            double stepA = (g1 * h22 - g2 * h12) / det;
            double stepB = (g2 * h11 - g1 * h12) / det;

            a -= stepA;
            b -= stepB;

            if (Math.Max(Math.Abs(stepA), Math.Abs(stepB)) < tolerance)
            {
                break;
            }
        }

        return new CalibrationModel(CalibrationKind.Platt, a, b, Array.Empty<double>(), Array.Empty<double>(), reliabilityBins);
    }

    public static CalibrationModel FitIsotonic(IEnumerable<(double Probability, bool Label)> samples, int reliabilityBins = 10)
    {
        var sorted = samples
            .Select(s => (Probability: ClampProbability(s.Probability), Label: s.Label ? 1.0 : 0.0))
            .OrderBy(s => s.Probability)
            .ToList();

        if (sorted.Count == 0)
        {
            return Identity(reliabilityBins);
        }

        var blocks = new List<Block>();
        foreach (var sample in sorted)
        {
            blocks.Add(new Block(sample.Probability, sample.Label, 1));
            while (blocks.Count >= 2 && blocks[^2].MeanY > blocks[^1].MeanY)
            {
                var merged = Block.Merge(blocks[^2], blocks[^1]);
                blocks.RemoveAt(blocks.Count - 1);
                blocks[^1] = merged;
            }
        }

        var xs = new List<double>();
        var ys = new List<double>();
        foreach (var block in blocks)
        {
            xs.Add(block.MeanX);
            ys.Add(block.MeanY);
        }

        // Ensure coverage across [0,1]
        if (xs[0] > 0)
        {
            xs.Insert(0, 0);
            ys.Insert(0, ys[0]);
        }

        if (xs[^1] < 1)
        {
            xs.Add(1);
            ys.Add(ys[^1]);
        }

        return new CalibrationModel(CalibrationKind.Isotonic, 1, 0, xs, ys, reliabilityBins);
    }

    public double Apply(double probability)
    {
        double p = ClampProbability(probability);
        return Kind switch
        {
            CalibrationKind.Platt => ApplyPlatt(p),
            CalibrationKind.Isotonic => ApplyIsotonic(p),
            _ => p
        };
    }

    public static double ComputeBrierScore(IEnumerable<(double Probability, bool Label)> samples, Func<double, double>? calibrator = null)
    {
        calibrator ??= p => p;
        var list = samples.ToList();
        if (list.Count == 0)
        {
            return double.NaN;
        }

        return list.Average(s =>
        {
            double calibrated = calibrator(s.Probability);
            double truth = s.Label ? 1.0 : 0.0;
            return Math.Pow(calibrated - truth, 2);
        });
    }

    public static double ComputeExpectedCalibrationError(IEnumerable<(double Probability, bool Label)> samples, int binCount, Func<double, double>? calibrator = null)
    {
        calibrator ??= p => p;
        var list = samples.ToList();
        if (list.Count == 0 || binCount <= 0)
        {
            return double.NaN;
        }

        double binSize = 1.0 / binCount;
        double eceSum = 0;
        int total = list.Count;
        for (int bin = 0; bin < binCount; bin++)
        {
            double start = bin * binSize;
            double end = (bin + 1) * binSize;
            var binSamples = list
                .Select(s => (Value: calibrator(s.Probability), s.Label))
                .Where(s =>
                    (s.Value >= start && s.Value < end) ||
                    (bin == binCount - 1 && s.Value == end))
                .ToList();
            if (binSamples.Count == 0)
            {
                continue;
            }

            double avgPred = binSamples.Average(s => s.Value);
            double freq = binSamples.Average(s => s.Label ? 1.0 : 0.0);
            double weight = (double)binSamples.Count / total;
            eceSum += weight * Math.Abs(freq - avgPred);
        }

        return eceSum;
    }

    public static IReadOnlyList<ReliabilityBin> BuildReliabilityBins(IEnumerable<(double Probability, bool Label)> samples, int binCount, Func<double, double>? calibrator = null)
    {
        calibrator ??= p => p;
        var list = samples.ToList();
        if (list.Count == 0)
        {
            return Array.Empty<ReliabilityBin>();
        }

        double binSize = 1.0 / binCount;
        var bins = new List<ReliabilityBin>();
        for (int bin = 0; bin < binCount; bin++)
        {
            double start = bin * binSize;
            double end = (bin + 1) * binSize;
            var binSamples = list
                .Select(s => (Value: calibrator(s.Probability), s.Label))
                .Where(s =>
                    (bin == binCount - 1 && s.Value <= end && s.Value >= start) ||
                    (s.Value >= start && s.Value < end))
                .ToList();

            if (binSamples.Count == 0)
            {
                bins.Add(new ReliabilityBin(start, end, 0, double.NaN, double.NaN));
                continue;
            }

            double meanPred = binSamples.Average(s => calibrator(s.Probability));
            double freq = binSamples.Average(s => s.Label ? 1.0 : 0.0);
            bins.Add(new ReliabilityBin(start, end, binSamples.Count, meanPred, freq));
        }

        return bins;
    }

    private double ApplyPlatt(double probability)
    {
        double logit = Logit(probability);
        double z = Slope * logit + Intercept;
        return 1.0 / (1.0 + Math.Exp(-z));
    }

    private double ApplyIsotonic(double probability)
    {
        if (IsotonicX.Count == 0 || IsotonicY.Count == 0)
        {
            return probability;
        }

        if (probability <= IsotonicX[0])
        {
            return IsotonicY[0];
        }

        for (int i = 1; i < IsotonicX.Count; i++)
        {
            if (probability <= IsotonicX[i])
            {
                double t = (probability - IsotonicX[i - 1]) / (IsotonicX[i] - IsotonicX[i - 1]);
                double value = IsotonicY[i - 1] + t * (IsotonicY[i] - IsotonicY[i - 1]);
                return ClampProbability(value);
            }
        }

        return IsotonicY[^1];
    }

    private static double ClampProbability(double probability)
    {
        if (double.IsNaN(probability) || double.IsInfinity(probability))
        {
            return 0.5;
        }

        return Math.Min(1 - 1e-9, Math.Max(1e-9, probability));
    }

    private static double Logit(double p)
    {
        double clamped = ClampProbability(p);
        return Math.Log(clamped / (1 - clamped));
    }

    private readonly record struct Block(double SumX, double SumY, int Count)
    {
        public double MeanX => SumX / Count;
        public double MeanY => SumY / Count;

        public static Block Merge(Block a, Block b)
        {
            return new Block(a.SumX + b.SumX, a.SumY + b.SumY, a.Count + b.Count);
        }
    }
}

public readonly record struct ReliabilityBin(double Start, double End, int Count, double MeanPredictedProbability, double EmpiricalFraction);
