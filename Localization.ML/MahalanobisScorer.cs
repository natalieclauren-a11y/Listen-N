using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace Localization.ML;

public sealed class MahalanobisScorer
{
    public IReadOnlyList<double> Mean { get; }
    public Matrix4x4 InverseCovariance { get; }

    public MahalanobisScorer(IReadOnlyList<double> mean, Matrix4x4 inverseCovariance)
    {
        if (mean.Count != 4)
        {
            throw new ArgumentException("Mahalanobis scorer expects 4D feature mean", nameof(mean));
        }

        Mean = mean;
        InverseCovariance = inverseCovariance;
    }

    public double Score(IReadOnlyList<double> vector)
    {
        if (vector.Count != 4)
        {
            throw new ArgumentException("Mahalanobis scorer expects 4D vectors", nameof(vector));
        }

        var diff = new Vector4((float)(vector[0] - Mean[0]), (float)(vector[1] - Mean[1]), (float)(vector[2] - Mean[2]), (float)(vector[3] - Mean[3]));
        var transformed = Vector4.Transform(diff, InverseCovariance);
        double distanceSquared = Vector4.Dot(diff, transformed);
        return Math.Sqrt(Math.Max(0, distanceSquared));
    }

    public static MahalanobisScorer FromSamples(IEnumerable<IReadOnlyList<double>> samples)
    {
        var sampleList = samples.ToList();
        if (sampleList.Count == 0)
        {
            throw new InvalidOperationException("Cannot fit Mahalanobis scorer without samples");
        }

        foreach (var sample in sampleList)
        {
            if (sample.Count != 4)
            {
                throw new ArgumentException("All samples must be 4D vectors", nameof(samples));
            }
        }

        double[] mean = new double[4];
        foreach (var sample in sampleList)
        {
            for (int i = 0; i < 4; i++)
            {
                mean[i] += sample[i];
            }
        }

        for (int i = 0; i < 4; i++)
        {
            mean[i] /= sampleList.Count;
        }

        float[,] cov = new float[4, 4];
        foreach (var sample in sampleList)
        {
            var centered = sample.Select((v, idx) => (float)(v - mean[idx])).ToArray();
            for (int i = 0; i < 4; i++)
            {
                for (int j = 0; j < 4; j++)
                {
                    cov[i, j] += centered[i] * centered[j];
                }
            }
        }

        float denom = Math.Max(1, sampleList.Count - 1);
        for (int i = 0; i < 4; i++)
        {
            for (int j = 0; j < 4; j++)
            {
                cov[i, j] /= denom;
                if (i == j)
                {
                    cov[i, j] += 1e-6f; // regularization
                }
            }
        }

        var covariance = new Matrix4x4(
            cov[0,0], cov[0,1], cov[0,2], cov[0,3],
            cov[1,0], cov[1,1], cov[1,2], cov[1,3],
            cov[2,0], cov[2,1], cov[2,2], cov[2,3],
            cov[3,0], cov[3,1], cov[3,2], cov[3,3]
        );

        if (!Matrix4x4.Invert(covariance, out var inverse))
        {
            throw new InvalidOperationException("Could not invert covariance matrix for Mahalanobis distance");
        }

        return new MahalanobisScorer(mean, inverse);
    }
}
