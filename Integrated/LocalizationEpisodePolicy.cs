using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Integrated.Contracts;

namespace Integrated.Runtime
{
    public sealed record LocalizationRow
    {
        public required double DurationSeconds { get; init; }
        public required IReadOnlyList<double> Channels { get; init; }
        public DateTimeOffset? WindowStartUtc { get; init; }
        public DateTimeOffset? WindowEndUtc { get; init; }
    }

    public sealed record LocalizationRequest
    {
        public required Guid EpisodeId { get; init; }
        public required bool IsProbe { get; init; }
        public required LocalizationRow Row { get; init; }
        public required DateTimeOffset EpisodeStartUtc { get; init; }
        public required DateTimeOffset EpisodeCurrentEndUtc { get; init; }
    }

    public sealed record LocalizationPrediction
    {
        public required bool IsOutOfDistribution { get; init; }
        public required double MahalanobisDistance { get; init; }
        public required double ClassifierProbability { get; init; }
        public required string Label { get; init; }
        public required double[] PredictedVector { get; init; }
    }

    public sealed record LocalizationStatus
    {
        public required string Code { get; init; }
        public Guid? EpisodeId { get; init; }
        public DateTimeOffset? TimestampUtc { get; init; }
        public string? Message { get; init; }
    }

    public static class LocalizationStatusCodes
    {
        public const string EpisodeStarted = "EpisodeStarted";
        public const string EpisodeCancelledByRecovery = "EpisodeCancelledByRecovery";
        public const string PublishCandidate = "PublishCandidate";
        public const string ProbeRequested = "ProbeRequested";
        public const string FinalRequestScheduled = "FinalRequestScheduled";
        public const string EpisodeCompleted = "EpisodeCompleted";
        public const string TriggerPolicyThresholdsDefaulted = "TriggerPolicyThresholdsDefaulted";
    }

    public sealed record LocalizationEpisodePolicyConfig
    {
        public int ConfuseDebounceWindows { get; init; } = 2;
        public int RecoverDebounceWindows { get; init; } = 2;
        public double QualityMin { get; init; } = 3.0;
        public int CheckEveryCounts { get; init; } = 2000;
        public double MinPublishDurationSeconds { get; init; } = 30.0;
        public double MaxPublishDurationSeconds { get; init; } = 60.0;
        public int ThrashWindowCount { get; init; } = 6;
        public int ThrashChangeThreshold { get; init; } = 3;
    }

    public sealed record TriggerPolicyThresholds
    {
        public int N_min_15cm_single { get; init; } = 20000;
        public int N_min_15cm_dual { get; init; } = 40000;

        public static TriggerPolicyThresholds Defaults => new();

        public static (TriggerPolicyThresholds Thresholds, bool UsedDefaults) Load(string? artifactsDirectory)
        {
            if (string.IsNullOrWhiteSpace(artifactsDirectory))
            {
                return (Defaults, true);
            }

            var path = Path.Combine(artifactsDirectory, "trigger_policy.json");
            if (!File.Exists(path))
            {
                return (Defaults, true);
            }

            try
            {
                var json = File.ReadAllText(path);
                var parsed = JsonSerializer.Deserialize<TriggerPolicyThresholds>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

                if (parsed is null)
                {
                    return (Defaults, true);
                }

                bool usedDefaults = false;
                int single = parsed.N_min_15cm_single > 0 ? parsed.N_min_15cm_single : Defaults.N_min_15cm_single;
                int dual = parsed.N_min_15cm_dual > 0 ? parsed.N_min_15cm_dual : Defaults.N_min_15cm_dual;
                if (single == Defaults.N_min_15cm_single || dual == Defaults.N_min_15cm_dual)
                {
                    usedDefaults = true;
                }

                return (new TriggerPolicyThresholds
                {
                    N_min_15cm_single = single,
                    N_min_15cm_dual = dual
                }, usedDefaults);
            }
            catch (Exception)
            {
                return (Defaults, true);
            }
        }
    }

    public sealed class LocalizationEpisodePolicy
    {
        private static readonly HashSet<string> ConfusedStates = new(StringComparer.OrdinalIgnoreCase)
        {
            "Hold",
            "Degraded",
            "LowRate",
            "Poisson"
        };

        private readonly LocalizationEpisodePolicyConfig _config;
        private readonly TriggerPolicyThresholds _thresholds;
        private readonly double[] _accumCounts = new double[15];
        private readonly double[] _pendingCounts = new double[15];
        private readonly List<string> _stateHistory;

        private Guid _episodeId;
        private DateTimeOffset _episodeStartUtc;
        private DateTimeOffset _episodeCurrentEndUtc;
        private double _accumDuration;
        private double _accumTotalCounts;
        private bool _episodeActive;
        private bool _probePending;
        private bool _finalRequested;
        private bool _finalPending;
        private bool _publishCandidate;
        private int _confusedStreak;
        private int _recoveryStreak;
        private double _countsAtLastRequest;
        private double _pendingDuration;
        private double _pendingTotalCounts;
        private DateTimeOffset _pendingStartUtc;
        private bool _thresholdWarningPending;

        public LocalizationEpisodePolicy(LocalizationEpisodePolicyConfig? config = null, string? artifactsDirectory = null)
        {
            _config = config ?? new LocalizationEpisodePolicyConfig();
            var (thresholds, usedDefaults) = TriggerPolicyThresholds.Load(artifactsDirectory);
            _thresholds = thresholds;
            _thresholdWarningPending = usedDefaults;
            _stateHistory = new List<string>(_config.ThrashWindowCount);
        }

        public event Action<LocalizationRequest>? OnRequestMl;
        public event Action<LocalizationStatus>? OnStatus;

        internal bool IsEpisodeActive => _episodeActive;
        internal double AccumulatedCountsTotal => _accumTotalCounts;

        public void AddWindow(RtWindowSummary w)
        {
            EmitThresholdWarningIfNeeded();
            UpdateStateHistory(w.RtState);

            bool isConfused = IsConfused(w);
            bool addedToEpisode = false;

            if (!_episodeActive)
            {
                if (isConfused)
                {
                    if (_confusedStreak == 0)
                    {
                        _pendingStartUtc = w.WindowStartUtc;
                        _pendingDuration = 0;
                        _pendingTotalCounts = 0;
                        Array.Clear(_pendingCounts, 0, _pendingCounts.Length);
                    }

                    _confusedStreak++;
                    AccumulateWindow(w, _pendingCounts, ref _pendingDuration, ref _pendingTotalCounts);

                    if (_confusedStreak >= _config.ConfuseDebounceWindows)
                    {
                        StartEpisodeFromPending(w.WindowEndUtc);
                        addedToEpisode = true;
                    }
                }
                else
                {
                    _confusedStreak = 0;
                    _pendingDuration = 0;
                    _pendingTotalCounts = 0;
                }
            }

            if (_episodeActive)
            {
                if (!addedToEpisode)
                {
                    AccumulateWindow(w, _accumCounts, ref _accumDuration, ref _accumTotalCounts);
                    _episodeCurrentEndUtc = w.WindowEndUtc;
                }

                if (isConfused)
                {
                    _recoveryStreak = 0;
                }
                else
                {
                    _recoveryStreak++;
                    if (_recoveryStreak >= _config.RecoverDebounceWindows && !_publishCandidate)
                    {
                        EmitStatus(LocalizationStatusCodes.EpisodeCancelledByRecovery, _episodeId, "Recovered before publish readiness.");
                        ResetEpisode();
                        return;
                    }
                }

                ScheduleRequestsIfNeeded();
            }
        }

        public void OnMlResult(LocalizationRequest req, LocalizationPrediction pred)
        {
            if (!_episodeActive || req.EpisodeId != _episodeId)
            {
                return;
            }

            if (req.IsProbe)
            {
                _probePending = false;
            }
            else
            {
                _finalPending = false;
            }

            EvaluatePublishCandidate(req, pred);

            if (!req.IsProbe && _finalRequested)
            {
                EmitStatus(LocalizationStatusCodes.EpisodeCompleted, _episodeId, "Final request completed.");
                ResetEpisode();
            }
        }

        private void EvaluatePublishCandidate(LocalizationRequest req, LocalizationPrediction pred)
        {
            if (pred.IsOutOfDistribution)
            {
                return;
            }

            double duration = req.Row.DurationSeconds;
            if (duration < _config.MinPublishDurationSeconds || duration > _config.MaxPublishDurationSeconds)
            {
                return;
            }

            double totalCounts = 0;
            foreach (var value in req.Row.Channels)
            {
                totalCounts += value;
            }

            int threshold = GetCountThreshold(pred.Label);
            if (threshold <= 0)
            {
                return;
            }

            if (totalCounts >= threshold)
            {
                _publishCandidate = true;
                EmitStatus(LocalizationStatusCodes.PublishCandidate, _episodeId, $"Counts={totalCounts:0}, Label={pred.Label}");
            }
        }

        private int GetCountThreshold(string label)
        {
            if (label.StartsWith("Dual", StringComparison.OrdinalIgnoreCase))
            {
                return _thresholds.N_min_15cm_dual;
            }

            if (label.StartsWith("Single", StringComparison.OrdinalIgnoreCase))
            {
                return _thresholds.N_min_15cm_single;
            }

            return 0;
        }

        private void ScheduleRequestsIfNeeded()
        {
            if (!_finalRequested && _accumDuration >= _config.MaxPublishDurationSeconds)
            {
                var request = BuildRequest(isProbe: false);
                _finalRequested = true;
                _finalPending = true;
                _countsAtLastRequest = _accumTotalCounts;
                OnRequestMl?.Invoke(request);
                EmitStatus(LocalizationStatusCodes.FinalRequestScheduled, _episodeId, "Hard stop reached.");
                return;
            }

            if (_finalRequested)
            {
                return;
            }

            if (_probePending)
            {
                return;
            }

            if (_accumTotalCounts - _countsAtLastRequest >= _config.CheckEveryCounts)
            {
                var request = BuildRequest(isProbe: true);
                _probePending = true;
                _countsAtLastRequest = _accumTotalCounts;
                OnRequestMl?.Invoke(request);
                EmitStatus(LocalizationStatusCodes.ProbeRequested, _episodeId, "Counts threshold reached.");
            }
        }

        private LocalizationRequest BuildRequest(bool isProbe)
        {
            var channels = new double[_accumCounts.Length];
            Array.Copy(_accumCounts, channels, channels.Length);
            var row = new LocalizationRow
            {
                DurationSeconds = _accumDuration,
                Channels = channels,
                WindowStartUtc = _episodeStartUtc,
                WindowEndUtc = _episodeCurrentEndUtc
            };

            return new LocalizationRequest
            {
                EpisodeId = _episodeId,
                IsProbe = isProbe,
                Row = row,
                EpisodeStartUtc = _episodeStartUtc,
                EpisodeCurrentEndUtc = _episodeCurrentEndUtc
            };
        }

        private void StartEpisodeFromPending(DateTimeOffset episodeEnd)
        {
            _episodeId = Guid.NewGuid();
            _episodeStartUtc = _pendingStartUtc;
            _episodeCurrentEndUtc = episodeEnd;
            _accumDuration = _pendingDuration;
            _accumTotalCounts = _pendingTotalCounts;
            Array.Copy(_pendingCounts, _accumCounts, _accumCounts.Length);
            _episodeActive = true;
            _recoveryStreak = 0;
            _publishCandidate = false;
            _probePending = false;
            _finalRequested = false;
            _finalPending = false;
            _countsAtLastRequest = 0;

            EmitStatus(LocalizationStatusCodes.EpisodeStarted, _episodeId, "Confusion debounce satisfied.");
        }

        private void ResetEpisode()
        {
            _episodeActive = false;
            _episodeId = Guid.Empty;
            _accumDuration = 0;
            _accumTotalCounts = 0;
            Array.Clear(_accumCounts, 0, _accumCounts.Length);
            _pendingDuration = 0;
            _pendingTotalCounts = 0;
            Array.Clear(_pendingCounts, 0, _pendingCounts.Length);
            _confusedStreak = 0;
            _recoveryStreak = 0;
            _probePending = false;
            _finalRequested = false;
            _finalPending = false;
            _publishCandidate = false;
            _countsAtLastRequest = 0;
        }

        private void AccumulateWindow(RtWindowSummary w, double[] targetCounts, ref double duration, ref double totalCounts)
        {
            duration += w.DurationSeconds;
            for (int i = 0; i < targetCounts.Length && i < w.Counts15.Length; i++)
            {
                targetCounts[i] += w.Counts15[i];
                totalCounts += w.Counts15[i];
            }
        }

        private bool IsConfused(RtWindowSummary w)
        {
            if (ConfusedStates.Contains(w.RtState))
            {
                return true;
            }

            if (!double.IsNaN(w.QualityScalar) && w.QualityScalar < _config.QualityMin)
            {
                return true;
            }

            return IsThrashing();
        }

        private void UpdateStateHistory(string state)
        {
            _stateHistory.Add(state ?? string.Empty);
            if (_stateHistory.Count > _config.ThrashWindowCount)
            {
                _stateHistory.RemoveAt(0);
            }
        }

        private bool IsThrashing()
        {
            if (_stateHistory.Count < 2)
            {
                return false;
            }

            int transitions = 0;
            for (int i = 1; i < _stateHistory.Count; i++)
            {
                if (!string.Equals(_stateHistory[i - 1], _stateHistory[i], StringComparison.OrdinalIgnoreCase))
                {
                    transitions++;
                }
            }

            return transitions >= _config.ThrashChangeThreshold;
        }

        private void EmitStatus(string code, Guid? episodeId, string? message)
        {
            OnStatus?.Invoke(new LocalizationStatus
            {
                Code = code,
                EpisodeId = episodeId,
                TimestampUtc = DateTimeOffset.UtcNow,
                Message = message
            });
        }

        private void EmitThresholdWarningIfNeeded()
        {
            if (!_thresholdWarningPending)
            {
                return;
            }

            _thresholdWarningPending = false;
            EmitStatus(LocalizationStatusCodes.TriggerPolicyThresholdsDefaulted, null, "Using default trigger thresholds.");
        }
    }
}
