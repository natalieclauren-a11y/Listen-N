using System;

namespace Integrated.Runtime;

public sealed record TriggerPolicyDocument
{
    public int Nmin_15cm_30s { get; init; }
    public int Nmin_15cm_60s { get; init; }
    public int ConfuseDebounceWindows { get; init; }
    public int RecoverDebounceWindows { get; init; }
    public double QualityMin { get; init; }
    public int CheckEveryCounts { get; init; }
    public double MinPublishDurationSeconds { get; init; }
    public double MaxPublishDurationSeconds { get; init; }
    public int ThrashWindowCount { get; init; }
    public int ThrashChangeThreshold { get; init; }
    public double StabilityToleranceCm { get; init; }
    public int StabilityK { get; init; }
    public double EarlyStopProbability { get; init; }
    public int EarlyStopK { get; init; }
    public double PublishProbabilityMin { get; init; }

    public (TriggerPolicyThresholds Thresholds, LocalizationEpisodePolicyConfig Config) ToPolicyInputs()
    {
        Validate();
        var thresholds = new TriggerPolicyThresholds
        {
            Nmin_15cm_30s = Nmin_15cm_30s,
            Nmin_15cm_60s = Nmin_15cm_60s
        };

        var config = new LocalizationEpisodePolicyConfig
        {
            ConfuseDebounceWindows = ConfuseDebounceWindows,
            RecoverDebounceWindows = RecoverDebounceWindows,
            QualityMin = QualityMin,
            CheckEveryCounts = CheckEveryCounts,
            MinPublishDurationSeconds = MinPublishDurationSeconds,
            MaxPublishDurationSeconds = MaxPublishDurationSeconds,
            ThrashWindowCount = ThrashWindowCount,
            ThrashChangeThreshold = ThrashChangeThreshold,
            StabilityToleranceCm = StabilityToleranceCm,
            StabilityK = StabilityK,
            EarlyStopProbability = EarlyStopProbability,
            EarlyStopK = EarlyStopK,
            PublishProbabilityMin = PublishProbabilityMin
        };

        return (thresholds, config);
    }

    private void Validate()
    {
        if (Nmin_15cm_30s <= 0 || Nmin_15cm_60s <= 0)
        {
            throw new InvalidOperationException("Trigger thresholds must be positive.");
        }

        if (ConfuseDebounceWindows <= 0 || RecoverDebounceWindows <= 0)
        {
            throw new InvalidOperationException("Debounce windows must be positive.");
        }

        if (QualityMin < 0)
        {
            throw new InvalidOperationException("QualityMin must be non-negative.");
        }

        if (CheckEveryCounts <= 0)
        {
            throw new InvalidOperationException("CheckEveryCounts must be positive.");
        }

        if (MinPublishDurationSeconds <= 0 || MaxPublishDurationSeconds <= 0 || MaxPublishDurationSeconds < MinPublishDurationSeconds)
        {
            throw new InvalidOperationException("Publish duration window is invalid.");
        }

        if (ThrashWindowCount <= 0 || ThrashChangeThreshold <= 0)
        {
            throw new InvalidOperationException("Thrash settings must be positive.");
        }

        if (StabilityToleranceCm <= 0 || StabilityK <= 0)
        {
            throw new InvalidOperationException("Stability settings must be positive.");
        }

        if (EarlyStopProbability <= 0 || EarlyStopProbability >= 1)
        {
            throw new InvalidOperationException("EarlyStopProbability must be between 0 and 1.");
        }

        if (EarlyStopK <= 0)
        {
            throw new InvalidOperationException("EarlyStopK must be positive.");
        }

        if (PublishProbabilityMin <= 0 || PublishProbabilityMin >= 1)
        {
            throw new InvalidOperationException("PublishProbabilityMin must be between 0 and 1.");
        }
    }
}
