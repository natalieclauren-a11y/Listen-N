using System;
using System.IO;
using System.Text;
using Localization.ML;

namespace Localization.Train;

internal static class LockedSchemaTexWriter
{
    public static string BuildLockedFeatureSchemaTableTex(FeatureBuilder builder, string caption, string label)
    {
        if (builder == null)
        {
            throw new ArgumentNullException(nameof(builder));
        }

        var sb = new StringBuilder();
        sb.AppendLine("\\begin{table}[t]");
        sb.AppendLine("\\centering");
        sb.AppendLine("\\small");
        sb.AppendLine("\\begin{tabular}{rllll}");
        sb.AppendLine("\\toprule");
        sb.AppendLine("Index & Feature name & Description & Definition / normalization & Dim.\\\\");
        sb.AppendLine("\\midrule");

        for (int i = 0; i < builder.FeatureNames.Count; i++)
        {
            string featureName = builder.FeatureNames[i];
            var (description, definition, dimension) = Describe(featureName);
            sb.AppendLine($"{i + 1} & {EscapeLatex(featureName)} & {description} & {definition} & {dimension}\\\\");
        }

        sb.AppendLine("\\bottomrule");
        sb.AppendLine("\\end{tabular}");
        sb.AppendLine($"\\caption{{{caption}}}");
        sb.AppendLine($"\\label{{{label}}}");
        sb.AppendLine("\\end{table}");
        return sb.ToString();
    }

    public static void WriteTo(string path, string tex)
    {
        if (path == null)
        {
            throw new ArgumentNullException(nameof(path));
        }

        if (tex == null)
        {
            throw new ArgumentNullException(nameof(tex));
        }

        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, tex);
    }

    private static (string Description, string Definition, string Dimension) Describe(string featureName)
    {
        if (featureName.StartsWith("Channel", StringComparison.Ordinal) && featureName.Contains("_norm", StringComparison.Ordinal))
        {
            string channel = featureName.Substring("Channel".Length).Replace("_norm", string.Empty, StringComparison.Ordinal);
            return ($"Normalized channel fraction for tube {channel}", "Channel_{channel} / TotalCounts (0 if TotalCounts=0)", "1");
        }

        if (featureName.StartsWith("Channel", StringComparison.Ordinal))
        {
            string channel = featureName.Substring("Channel".Length);
            return ($"Counts in detector tube {channel} within analysis window", "Raw counts accumulated over window", "1");
        }

        return featureName switch
        {
            "TotalCounts" => ("Total counts across all detector channels", "Sum of per-channel counts", "1"),
            "Entropy" => ("Spatial dispersion of normalized counts", "Shannon entropy of normalized channel fractions", "1"),
            "Gini" => ("Spatial concentration metric", "1 - sum(p_i^2) over normalized fractions", "1"),
            "Anisotropy" => ("Second-order spatial variance", "Variance of normalized channel fractions", "1"),
            "DipoleMagnitude" => ("First-order spatial asymmetry", "Norm of sum(p_i * position_i)", "1"),
            "Duration_s" => ("Analysis window duration", "Wall-clock duration associated with window", "1"),
            _ => throw new InvalidOperationException($"Unhandled feature name '{featureName}' for locked schema export")
        };
    }

    private static string EscapeLatex(string input)
    {
        return input.Replace("_", "\\_", StringComparison.Ordinal);
    }
}
