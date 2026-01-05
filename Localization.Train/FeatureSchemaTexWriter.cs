using System;
using System.IO;
using System.Text;
using Localization.ML;

namespace Localization.Train;

internal static class FeatureSchemaTexWriter
{
    public static string BuildRawFeatureSchemaTableTex(FeatureBuilder builder, string caption, string label)
    {
        if (builder == null)
        {
            throw new ArgumentNullException(nameof(builder));
        }

        var sb = new StringBuilder();
        sb.AppendLine("\\begin{table}[t]");
        sb.AppendLine("\\centering");
        sb.AppendLine("\\small");
        sb.AppendLine("\\begin{tabular}{rlll}");
        sb.AppendLine("\\toprule");
        sb.AppendLine("Index & Feature name & Description & Units/Notes\\\\");
        sb.AppendLine("\\midrule");

        for (int i = 0; i < builder.FeatureNames.Count; i++)
        {
            string featureName = builder.FeatureNames[i];
            var (description, units) = Describe(featureName);
            sb.AppendLine($"{i + 1} & {EscapeLatex(featureName)} & {description} & {units}\\\\");
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

    private static (string Description, string Units) Describe(string featureName)
    {
        if (featureName.StartsWith("Channel", StringComparison.Ordinal) && featureName.Contains("_norm", StringComparison.Ordinal))
        {
            string channel = featureName.Substring("Channel".Length).Replace("_norm", string.Empty, StringComparison.Ordinal);
            return ($"Channel {channel} fraction of total counts", "unitless, in [0,1] (0 if TotalCounts=0)");
        }

        if (featureName.StartsWith("Channel", StringComparison.Ordinal))
        {
            string channel = featureName.Substring("Channel".Length);
            return ($"Counts in tube {channel} within analysis window", "counts");
        }

        return featureName switch
        {
            "TotalCounts" => ("Sum over all tubes in window", "counts"),
            "Entropy" => ("Shannon entropy of normalized channel fractions", "unitless"),
            "Gini" => ("1 - sum(p_i^2) over normalized fractions", "unitless"),
            "Anisotropy" => ("Variance of normalized fractions across channels", "unitless"),
            "DipoleMagnitude" => ("|sum(p_i * position_i)| using DipolePositions", "unitless (position-weighted)"),
            "Duration_s" => ("Window duration", "s (may be NaN if not provided)"),
            _ => throw new InvalidOperationException($"Unhandled feature name '{featureName}' for LaTeX export")
        };
    }

    private static string EscapeLatex(string input)
    {
        return input.Replace("_", "\\_", StringComparison.Ordinal);
    }
}
