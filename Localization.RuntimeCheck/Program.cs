using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using Localization.ML;

namespace Localization.RuntimeCheck;

internal static class Program
{
    private sealed record Options
    {
        public required string ArtifactsDir { get; init; }
        public required string InputPath { get; init; }
        public string? Format { get; init; }
        public bool StrictSchema { get; init; } = true;
        public bool PrintFeatures { get; init; }
    }

    public static int Main(string[] args)
    {
        var options = ParseArgs(args);
        if (options == null)
        {
            PrintUsage();
            return 1;
        }

        var artifactsDir = Path.GetFullPath(options.ArtifactsDir);
        var configPath = Path.Combine(artifactsDir, "pipeline_config.json");
        if (!File.Exists(configPath))
        {
            Console.Error.WriteLine($"Missing pipeline configuration at {configPath}");
            return 1;
        }

        PipelineConfiguration config;
        try
        {
            config = JsonSerializer.Deserialize<PipelineConfiguration>(File.ReadAllText(configPath))
                     ?? throw new InvalidOperationException("Missing pipeline configuration contents");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to read pipeline configuration: {ex.Message}");
            return 1;
        }

        var featureBuilder = new FeatureBuilder(config.Epsilon, config.DipolePositions);
        if (config.FeatureColumns.Count == 0)
        {
            config.FeatureColumns = featureBuilder.FeatureNames;
        }

        config.SchemaVersion = string.IsNullOrWhiteSpace(config.SchemaVersion)
            ? SchemaStampBuilder.DefaultSchemaVersion
            : config.SchemaVersion;

        var classifierTrainer = string.IsNullOrWhiteSpace(config.ClassifierTrainer)
            ? "FastForestBinary"
            : config.ClassifierTrainer!;
        var regressorTrainers = config.RegressorTrainers?.ToArray() ?? new[] { "FastForestRegression", "FastForestRegression" };

        var stamp = SchemaStampBuilder.Build(config, featureBuilder, classifierTrainer, regressorTrainers);
        var runtimeHash = SchemaStampBuilder.ComputeHash(stamp);
        var storedHash = config.SchemaHash;

        Console.WriteLine("=== Artifact manifest ===");
        Console.WriteLine($"SchemaVersion: {config.SchemaVersion}");
        Console.WriteLine($"SchemaHash (artifact): {storedHash}");
        Console.WriteLine($"SchemaHash (runtime): {runtimeHash}");
        Console.WriteLine($"Classifier trainer: {classifierTrainer}");
        Console.WriteLine($"Regressor trainers: {string.Join(", ", regressorTrainers)}");
        Console.WriteLine($"Thresholds: StrictProbability={config.StrictProbability:F4}, OOD={config.OutOfDistributionThreshold:F4}, MinSeparationCm={config.MinimumSeparationCm:F2}");
        Console.WriteLine($"Models: classifier.zip, single_regressor, dual_regressor, mahalanobis.json");

        var mismatch = !string.IsNullOrWhiteSpace(storedHash) && !string.Equals(runtimeHash, storedHash, StringComparison.OrdinalIgnoreCase);
        if (mismatch && options.StrictSchema)
        {
            Console.Error.WriteLine("Schema hash mismatch and --strict-schema=true; aborting.");
            return 2;
        }

        if (mismatch)
        {
            Console.WriteLine("Warning: schema hash mismatch; continuing because --strict-schema=false.");
        }

        var pipeline = LocalizationPipeline.Load(artifactsDir);
        var (row, features) = LoadInput(options.InputPath, options.Format, featureBuilder);
        var prediction = pipeline.Predict(row);

        PrintReport(prediction, config, features, featureBuilder.FeatureNames, options.PrintFeatures);
        return 0;
    }

    private static Options? ParseArgs(IReadOnlyList<string> args)
    {
        string? artifacts = null;
        string? input = null;
        string? format = null;
        bool strict = true;
        bool printFeatures = false;

        for (int i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--artifacts-dir" when i + 1 < args.Count:
                    artifacts = args[++i];
                    break;
                case "--input" when i + 1 < args.Count:
                    input = args[++i];
                    break;
                case "--format" when i + 1 < args.Count:
                    format = args[++i];
                    break;
                case "--strict-schema" when i + 1 < args.Count:
                    strict = bool.Parse(args[++i]);
                    break;
                case "--print-features" when i + 1 < args.Count:
                    printFeatures = bool.Parse(args[++i]);
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(artifacts) || string.IsNullOrWhiteSpace(input))
        {
            return null;
        }

        return new Options
        {
            ArtifactsDir = artifacts!,
            InputPath = input!,
            Format = format,
            StrictSchema = strict,
            PrintFeatures = printFeatures
        };
    }

    private static (LocalizationRow Row, FeatureComputationResult Features) LoadInput(string path, string? format, FeatureBuilder featureBuilder)
    {
        var resolvedFormat = InferFormat(path, format);
        return resolvedFormat switch
        {
            "json" => LoadJson(path, featureBuilder),
            "csv" => LoadCsv(path, featureBuilder),
            _ => throw new InvalidOperationException($"Unsupported input format: {resolvedFormat}")
        };
    }

    private static string InferFormat(string path, string? format)
    {
        if (!string.IsNullOrWhiteSpace(format))
        {
            return format.ToLowerInvariant();
        }

        var ext = Path.GetExtension(path);
        return string.Equals(ext, ".json", StringComparison.OrdinalIgnoreCase) ? "json" : "csv";
    }

    private static (LocalizationRow, FeatureComputationResult) LoadJson(string path, FeatureBuilder featureBuilder)
    {
        var document = JsonDocument.Parse(File.ReadAllText(path));
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("Expected a single JSON object containing channel fields.");
        }

        var map = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetDouble(out var value))
            {
                map[property.Name] = value;
            }
        }

        return BuildRow(map, featureBuilder);
    }

    private static (LocalizationRow, FeatureComputationResult) LoadCsv(string path, FeatureBuilder featureBuilder)
    {
        var lines = File.ReadAllLines(path)
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .ToArray();
        if (lines.Length < 2)
        {
            throw new InvalidOperationException("CSV input must include a header row and at least one data row.");
        }

        var headers = lines[0].Split(',').Select(h => h.Trim()).ToArray();
        var values = lines[1].Split(',').Select(v => v.Trim()).ToArray();
        var map = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < headers.Length && i < values.Length; i++)
        {
            if (double.TryParse(values[i], NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed))
            {
                map[headers[i]] = parsed;
            }
        }

        return BuildRow(map, featureBuilder);
    }

    private static (LocalizationRow, FeatureComputationResult) BuildRow(IReadOnlyDictionary<string, double> map, FeatureBuilder featureBuilder)
    {
        var missingChannels = new List<string>();
        var channels = new double[FeatureBuilder.ChannelCount];
        for (int i = 1; i <= FeatureBuilder.ChannelCount; i++)
        {
            var key = $"Channel{i}";
            if (!map.TryGetValue(key, out var value))
            {
                missingChannels.Add(key);
                continue;
            }

            channels[i - 1] = value;
        }

        if (missingChannels.Count > 0)
        {
            throw new InvalidOperationException($"Missing required channel fields: {string.Join(", ", missingChannels)}");
        }

        double? duration = null;
        if (map.TryGetValue("duration_s", out var durationSeconds) || map.TryGetValue("DurationSeconds", out durationSeconds))
        {
            duration = durationSeconds;
        }

        var features = featureBuilder.BuildFeatures(channels, duration);
        var row = new LocalizationRow
        {
            Channels = channels,
            DurationSeconds = duration,
            IsDual = false
        };

        return (row, features);
    }

    private static void PrintReport(PredictionResult prediction, PipelineConfiguration config, FeatureComputationResult features, IReadOnlyList<string> featureNames, bool printFeatures)
    {
        Console.WriteLine();
        Console.WriteLine("=== Prediction report ===");
        Console.WriteLine($"Outcome: {prediction.Label}");
        Console.WriteLine($"Classifier: probability={prediction.RawClassification.Probability:F4}, score={prediction.RawClassification.Score:F4}, strict threshold={config.StrictProbability:F4}");
        Console.WriteLine($"OOD: distance={prediction.Diagnostics.MahalanobisDistance:F4}, threshold={config.OutOfDistributionThreshold:F4}, pass={(prediction.Diagnostics.IsOutOfDistribution ? "FAIL" : "PASS")}");

        if (prediction.Coordinates.Count >= 6)
        {
            var first = prediction.Coordinates.Take(3).ToArray();
            var second = prediction.Coordinates.Skip(3).Take(3).ToArray();
            var separation = Math.Sqrt(first.Zip(second).Sum(p => Math.Pow(p.First - p.Second, 2)));
            Console.WriteLine($"Dual separation: {separation:F2} cm");
            Console.WriteLine($"Coordinates (cm): [{string.Join(", ", prediction.Coordinates.Select(c => c.ToString("F3", CultureInfo.InvariantCulture)))}]");
        }
        else if (prediction.Coordinates.Count >= 3)
        {
            Console.WriteLine($"Coordinates (cm): [{string.Join(", ", prediction.Coordinates.Take(3).Select(c => c.ToString("F3", CultureInfo.InvariantCulture)))}]");
        }

        if (printFeatures)
        {
            Console.WriteLine();
            Console.WriteLine("Feature vector:");
            for (int i = 0; i < features.FeatureVector.Length && i < featureNames.Count; i++)
            {
                Console.WriteLine($"  {featureNames[i]}: {features.FeatureVector[i]:G17}");
            }
        }
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Usage: dotnet run --project Localization.RuntimeCheck -- --artifacts-dir <path> --input <path> [--format csv|json] [--strict-schema true|false] [--print-features true|false]");
    }
}
