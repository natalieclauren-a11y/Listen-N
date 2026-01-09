using System;

namespace Integrated.Runtime
{
    public sealed record LocalizationOperatorStatus
    {
        public required Guid RunId { get; init; }
        public required LocalizationHealthState Health { get; init; }
        public required bool EpisodeActive { get; init; }
        public Guid? EpisodeId { get; init; }
        public double AccumulatedDurationSec { get; init; }
        public int AccumulatedCounts { get; init; }
        public required string RtState { get; init; }
        public required string LastDecisionKind { get; init; }
        public required string LastReasonCode { get; init; }
        public required DateTime LastDecisionUtc { get; init; }
        public string? LastOutcomeLabel { get; init; }
        public double? LastProbability { get; init; }
        public bool? LastIsOod { get; init; }
    }
}
