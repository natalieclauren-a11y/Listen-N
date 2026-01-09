using System;
using Integrated.Contracts;

namespace Integrated.Runtime
{
    public static class LocalizationDecisionRecordFactory
    {
        public static LocalizationDecisionRecord CreateRuntimeMisconfigured(Guid runId, string? detail = null)
        {
            return new LocalizationDecisionRecord
            {
                RunId = runId,
                TimestampUtc = DateTime.UtcNow,
                DecisionKind = LocalizationDecisionKind.Refuse,
                ReasonCode = LocalizationDecisionReasonCode.Refused_RuntimeMisconfigured,
                EpisodeId = Guid.Empty,
                EpisodeActive = false,
                RtContext = new LocalizationDecisionRtContext
                {
                    WindowDurationSeconds = 0,
                    WindowTotalCounts = 0,
                    RtStateName = string.Empty,
                    ConfusionMetric = double.NaN,
                    IsConfusedCandidate = false,
                    QualityScalar = double.NaN,
                    RateTotalCps = double.NaN
                },
                EpisodeContext = new LocalizationDecisionEpisodeContext
                {
                    AccumulatedDurationSeconds = 0,
                    AccumulatedTotalCounts = 0,
                    ConfusionDebounceCount = 0,
                    RecoveryDebounceCount = 0,
                    StabilityDeltaCm = double.NaN,
                    ConsecutiveStableCount = 0,
                    RequiredStableCount = 0
                },
                PolicyContext = new LocalizationDecisionPolicyContext
                {
                    MinPublishDurationSeconds = 0,
                    MaxPublishDurationSeconds = 0,
                    Nmin15cm_Single = 0,
                    Nmin15cm_Dual = 0,
                    ActiveThresholdUsed = 0,
                    ActiveThresholdMode = string.Empty,
                    CountsGatePassed = false,
                    DurationGatePassed = false,
                    OodGatePassed = false,
                    StabilityGatePassed = false,
                    ProbabilityGatePassed = false,
                    GatingReasons = detail is null ? Array.Empty<string>() : new[] { detail }
                },
                MlContext = null,
                Result = new LocalizationDecisionResult
                {
                    Outcome = LocalizationDecisionOutcome.Refused,
                    LowStatistics = false,
                    PublishedCoords = null
                }
            };
        }
    }
}
