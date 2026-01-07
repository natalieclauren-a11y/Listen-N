using System;
using System.Collections.Generic;

namespace Integrated.Contracts
{
    public enum LocalizationTriggerSource
    {
        Auto,
        Manual
    }

    public record AnalysisSnapshot(
        Guid SnapshotId,
        DateTime TimestampUtc,
        double WindowDurationSeconds,
        int[] TubeCounts,
        int TotalCounts,
        string FsmState,
        int SelectedGateMicroseconds,
        double Y,
        double SigmaY,
        double Zy,
        bool ChangeDetected,
        bool HoldLike,
        bool DegradedLike,
        int SchemaVersion,
        string SchemaHash,
        IDictionary<string, string>? Diagnostics);

    public sealed record RtWindowSummary
    {
        public required DateTimeOffset WindowStartUtc { get; init; }
        public required DateTimeOffset WindowEndUtc { get; init; }
        public required double DurationSeconds { get; init; }
        public required double[] Counts15 { get; init; }
        public required string RtState { get; init; }
        public required double RateTotalCps { get; init; }
        public required double QualityScalar { get; init; }
        public bool IsConfusedCandidate { get; init; }
    }

    public record LocalizationRow
    {
        public required float Duration { get; init; }
        public required IReadOnlyList<float> Channels { get; init; }
        public required int SchemaVersion { get; init; }
        public required string SchemaHash { get; init; }

        public static LocalizationRow FromSnapshot(AnalysisSnapshot snapshot)
        {
            if (snapshot.TubeCounts.Length != 15)
            {
                throw new ArgumentException("Expected 15 tube counts", nameof(snapshot));
            }

            var channels = new float[15];
            for (var i = 0; i < snapshot.TubeCounts.Length; i++)
            {
                channels[i] = snapshot.TubeCounts[i];
            }

            return new LocalizationRow
            {
                Duration = (float)snapshot.WindowDurationSeconds,
                Channels = channels,
                SchemaVersion = snapshot.SchemaVersion,
                SchemaHash = snapshot.SchemaHash
            };
        }
    }

    public record LocalizationResult
    {
        public required Guid SnapshotId { get; init; }
        public required DateTime TimestampUtc { get; init; }
        public bool Forced { get; init; }
        public required string TriggerReason { get; init; }
        public required LocalizationTriggerSource TriggerSource { get; init; }
        public string? PredictedLabel { get; init; }
        public float? PredictedDistance { get; init; }
        public float? PredictedAngle { get; init; }
        public float? PredictedDistance2 { get; init; }
        public float? PredictedAngle2 { get; init; }
        public float? Confidence { get; init; }
        public float? Confidence2 { get; init; }
        public bool Ood { get; init; }
        public float? Mahalanobis { get; init; }
        public string? RefusalReason { get; init; }
        public string? ModelVersion { get; init; }
        public string? FeatureVersion { get; init; }
        public IDictionary<string, string>? Diagnostics { get; init; }
    }

    public record LocalizationRequest
    {
        public Guid RequestId { get; init; } = Guid.NewGuid();
        public DateTime RequestedAtUtc { get; init; } = DateTime.UtcNow;
        public LocalizationTriggerSource TriggerSource { get; init; }
        public required string TriggerReason { get; init; }
        public Guid? RequestedSnapshotId { get; init; }
        public bool AllowPolicyBypass { get; init; }
        public IDictionary<string, string>? Metadata { get; init; }
    }

    public interface ILocalizationTriggerPolicy
    {
        bool ShouldTrigger(AnalysisSnapshot snapshot);
    }

    public sealed class DefaultLocalizationTriggerPolicy : ILocalizationTriggerPolicy
    {
        public const int MinimumTotalCounts = 25;
        public const double MinimumZy = 1.0;

        private static readonly HashSet<string> BlockedStates = new(StringComparer.OrdinalIgnoreCase)
        {
            "Hold",
            "LowRate",
            "Poisson",
            "Degraded"
        };

        public bool ShouldTrigger(AnalysisSnapshot snapshot)
        {
            if (BlockedStates.Contains(snapshot.FsmState))
            {
                return false;
            }

            if (snapshot.ChangeDetected)
            {
                return false;
            }

            if (snapshot.TotalCounts < MinimumTotalCounts)
            {
                return false;
            }

            if (snapshot.Zy < MinimumZy)
            {
                return false;
            }

            return true;
        }
    }

    public interface ILocalizationTrigger
    {
        System.Threading.Tasks.Task<Integrated.Runtime.LocalizationRequest> TriggerNowAsync(string reason, IDictionary<string, string>? tags = null);
    }
}
