using System;
using System.Collections.Generic;

namespace Integrated.Contracts
{
    public enum LocalizationDecisionKind
    {
        TriggerEvaluated,
        MlResultProcessed,
        Publish,
        Refuse,
        Defer,
        EpisodeStart,
        EpisodeEnd
    }

    public enum LocalizationDecisionOutcome
    {
        None,
        Published,
        Refused,
        Deferred
    }

    public enum LocalizationDecisionReasonCode
    {
        EpisodeStarted_ConfusionDebounced,
        EpisodeStarted_Manual,
        EpisodeEnded_ConfusionCleared,
        EpisodeEnded_Published,
        EpisodeEnded_Finalized,
        Deferred_AutoDisabled,
        Deferred_NotConfused,
        Deferred_WaitingForConfusionDebounce,
        Deferred_WaitingForCounts,
        Deferred_WaitingForDurationMin,
        Deferred_WaitingForStability,
        Deferred_WaitingForProbability,
        Deferred_Backpressure,
        Refused_OOD,
        Refused_OODAtMaxDuration,
        Refused_InvalidPredictionShape,
        Refused_MlTimeout,
        Refused_Cancelled,
        Refused_ArtifactsNotLoaded,
        Refused_MlException,
        Refused_RuntimeMisconfigured,
        Refused_QueueSaturated,
        Refused_Unknown,
        Published_Single,
        Published_Dual,
        Published_LowStatisticsAtMaxDuration,
        Published_OodAtMaxDuration,
        Published_EarlyStop
    }

    public sealed record LocalizationDecisionRecord
    {
        public required Guid RunId { get; init; }
        public required DateTime TimestampUtc { get; init; }
        public required LocalizationDecisionKind DecisionKind { get; init; }
        public required LocalizationDecisionReasonCode ReasonCode { get; init; }
        public required Guid EpisodeId { get; init; }
        public required bool EpisodeActive { get; init; }
        public required LocalizationDecisionRtContext RtContext { get; init; }
        public required LocalizationDecisionEpisodeContext EpisodeContext { get; init; }
        public required LocalizationDecisionPolicyContext PolicyContext { get; init; }
        public LocalizationDecisionMlContext? MlContext { get; init; }
        public required LocalizationDecisionResult Result { get; init; }
    }

    public sealed record LocalizationDecisionRtContext
    {
        public required double WindowDurationSeconds { get; init; }
        public required int WindowTotalCounts { get; init; }
        public required string RtStateName { get; init; }
        public required double ConfusionMetric { get; init; }
        public required bool IsConfusedCandidate { get; init; }
        public required double QualityScalar { get; init; }
        public required double RateTotalCps { get; init; }
    }

    public sealed record LocalizationDecisionEpisodeContext
    {
        public required double AccumulatedDurationSeconds { get; init; }
        public required double AccumulatedTotalCounts { get; init; }
        public required int ConfusionDebounceCount { get; init; }
        public required int RecoveryDebounceCount { get; init; }
        public required double StabilityDeltaCm { get; init; }
        public required int ConsecutiveStableCount { get; init; }
        public required int RequiredStableCount { get; init; }
    }

    public sealed record LocalizationDecisionPolicyContext
    {
        public required double MinPublishDurationSeconds { get; init; }
        public required double MaxPublishDurationSeconds { get; init; }
        public required int Nmin15cm_Single { get; init; }
        public required int Nmin15cm_Dual { get; init; }
        public required int ActiveThresholdUsed { get; init; }
        public required string ActiveThresholdMode { get; init; }
        public required bool CountsGatePassed { get; init; }
        public required bool DurationGatePassed { get; init; }
        public required bool OodGatePassed { get; init; }
        public required bool StabilityGatePassed { get; init; }
        public required bool ProbabilityGatePassed { get; init; }
        public required IReadOnlyList<string> GatingReasons { get; init; }
    }

    public sealed record LocalizationDecisionMlContext
    {
        public string? ModelId { get; init; }
        public double? InferenceTimeMs { get; init; }
        public required string OutcomeLabel { get; init; }
        public required double ClassifierProbability { get; init; }
        public required double MahalanobisDistance { get; init; }
        public required bool IsOutOfDistribution { get; init; }
        public required double[]? PredictedCoords { get; init; }
    }

    public sealed record LocalizationDecisionResult
    {
        public required LocalizationDecisionOutcome Outcome { get; init; }
        public required bool LowStatistics { get; init; }
        public double[]? PublishedCoords { get; init; }
    }
}
