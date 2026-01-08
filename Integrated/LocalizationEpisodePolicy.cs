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

    public sealed record LocalizationPublishResult
    {
        public required Guid EpisodeId { get; init; }
        public required DateTimeOffset EpisodeStartUtc { get; init; }
        public required DateTimeOffset EpisodeEndUtc { get; init; }
        public required string Label { get; init; }
        public required IReadOnlyList<double[]> Coordinates { get; init; }
        public required double Probability { get; init; }
        public required bool IsOod { get; init; }
        public required double MahalanobisDistance { get; init; }
        public required double TotalCounts { get; init; }
        public required double DurationSeconds { get; init; }
        public required bool LowStatistics { get; init; }
        public string? Reason { get; init; }
    }

    public sealed record LocalizationEvaluation
    {
        public required Guid EpisodeId { get; init; }
        public required bool IsProbe { get; init; }
        public required double DurationSeconds { get; init; }
        public required double TotalCounts { get; init; }
        public required string Label { get; init; }
        public required double Probability { get; init; }
        public required bool IsOod { get; init; }
        public required double MahalanobisDistance { get; init; }
        public required double Delta { get; init; }
        public required int StableCount { get; init; }
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
        public double StabilityToleranceCm { get; init; } = 5.0;
        public int StabilityK { get; init; } = 3;
        public double EarlyStopProbability { get; init; } = 0.98;
        public int EarlyStopK { get; init; } = 2;
        public double PublishProbabilityMin { get; init; } = 0.80;
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
        private int _stableCount;
        private double[]? _lastPrediction;
        private bool? _lastPredictionIsDual;
        private RtWindowSummary? _lastWindowSummary;
        private bool _lastIsConfused;
        private double _lastDelta = double.NaN;

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
        public event Action<LocalizationPublishResult>? OnPublish;

        public bool AutoModeEnabled { get; set; } = true;

        internal bool IsEpisodeActive => _episodeActive;
        internal double AccumulatedCountsTotal => _accumTotalCounts;
        internal double AccumulatedDurationSeconds => _accumDuration;
        internal bool LastIsConfused => _lastIsConfused;
        internal double LastStabilityDelta => _lastDelta;
        internal int StableCount => _stableCount;

        public LocalizationEvaluation? LastEvaluation { get; private set; }

        public void AddWindow(RtWindowSummary w)
        {
            _lastWindowSummary = w;
            EmitThresholdWarningIfNeeded();
            UpdateStateHistory(w.RtState);

            bool isConfused = IsConfused(w);
            _lastIsConfused = isConfused;
            bool addedToEpisode = false;

            if (!_episodeActive && !AutoModeEnabled)
            {
                _confusedStreak = 0;
                _pendingDuration = 0;
                _pendingTotalCounts = 0;
                Array.Clear(_pendingCounts, 0, _pendingCounts.Length);
                return;
            }

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

            var duration = req.Row.DurationSeconds;
            var totalCounts = GetTotalCounts(req.Row.Channels);
            var threshold = GetCountThreshold(pred.Label);
            bool withinPublishWindow = duration >= _config.MinPublishDurationSeconds && duration <= _config.MaxPublishDurationSeconds;
            bool countsGateMet = threshold > 0 && totalCounts >= threshold;

            if (withinPublishWindow && !pred.IsOutOfDistribution && countsGateMet && !_publishCandidate)
            {
                _publishCandidate = true;
                EmitStatus(LocalizationStatusCodes.PublishCandidate, _episodeId, $"Counts={totalCounts:0}, Label={pred.Label}");
            }

            if (!req.IsProbe && _finalRequested)
            {
                PublishFinalResult(req, pred, totalCounts, countsGateMet);
                EmitStatus(LocalizationStatusCodes.EpisodeCompleted, _episodeId, "Final request completed.");
                UpdateLastEvaluation(req, pred, totalCounts);
                ResetEpisode();
                return;
            }

            if (!withinPublishWindow || pred.IsOutOfDistribution)
            {
                ResetStabilityTracking();
                UpdateLastEvaluation(req, pred, totalCounts);
                return;
            }

            UpdateStabilityTracking(pred);
            UpdateLastEvaluation(req, pred, totalCounts);
            if (!_publishCandidate && !countsGateMet)
            {
                return;
            }

            if (ShouldPublish(pred))
            {
                EmitPublishResult(req, pred, totalCounts, lowStatistics: false, reason: null);
                ResetEpisode();
                return;
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

            if (_accumDuration < _config.MinPublishDurationSeconds)
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

        internal LocalizationRequest BuildManualProbeRequest()
        {
            if (!_episodeActive)
            {
                StartManualEpisode(DateTimeOffset.UtcNow, _lastWindowSummary);
            }

            var request = BuildRequest(isProbe: true);
            _probePending = true;
            _countsAtLastRequest = _accumTotalCounts;
            EmitStatus(LocalizationStatusCodes.ProbeRequested, _episodeId, "Manual probe requested.");
            return request;
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
            ResetStabilityTracking();

            EmitStatus(LocalizationStatusCodes.EpisodeStarted, _episodeId, "Confusion debounce satisfied.");
        }

        private void StartManualEpisode(DateTimeOffset episodeStart)
        {
            StartManualEpisode(episodeStart, seedWindow: null);
        }

        private void StartManualEpisode(DateTimeOffset episodeStart, RtWindowSummary? seedWindow)
        {
            _episodeId = Guid.NewGuid();
            _episodeStartUtc = seedWindow?.WindowStartUtc ?? episodeStart;
            _episodeCurrentEndUtc = seedWindow?.WindowEndUtc ?? episodeStart;
            _accumDuration = 0;
            _accumTotalCounts = 0;
            Array.Clear(_accumCounts, 0, _accumCounts.Length);
            if (seedWindow is not null)
            {
                AccumulateWindow(seedWindow, _accumCounts, ref _accumDuration, ref _accumTotalCounts);
            }
            _episodeActive = true;
            _confusedStreak = 0;
            _recoveryStreak = 0;
            _pendingDuration = 0;
            _pendingTotalCounts = 0;
            Array.Clear(_pendingCounts, 0, _pendingCounts.Length);
            _publishCandidate = false;
            _probePending = false;
            _finalRequested = false;
            _finalPending = false;
            _countsAtLastRequest = 0;
            ResetStabilityTracking();

            EmitStatus(LocalizationStatusCodes.EpisodeStarted, _episodeId, "Manual episode started.");
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
            ResetStabilityTracking();
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

        private void PublishFinalResult(LocalizationRequest req, LocalizationPrediction pred, double totalCounts, bool countsGateMet)
        {
            if (pred.IsOutOfDistribution)
            {
                EmitPublishResult(req, pred with { Label = "Unknown" }, totalCounts, lowStatistics: false, reason: "OOD at max duration");
                return;
            }

            bool lowStatistics = !countsGateMet;
            EmitPublishResult(req, pred, totalCounts, lowStatistics, reason: null);
        }

        private void EmitPublishResult(LocalizationRequest req, LocalizationPrediction pred, double totalCounts, bool lowStatistics, string? reason)
        {
            var coordinates = BuildCoordinates(pred);
            OnPublish?.Invoke(new LocalizationPublishResult
            {
                EpisodeId = req.EpisodeId,
                EpisodeStartUtc = req.EpisodeStartUtc,
                EpisodeEndUtc = req.EpisodeCurrentEndUtc,
                Label = pred.Label,
                Coordinates = coordinates,
                Probability = pred.ClassifierProbability,
                IsOod = pred.IsOutOfDistribution,
                MahalanobisDistance = pred.MahalanobisDistance,
                TotalCounts = totalCounts,
                DurationSeconds = req.Row.DurationSeconds,
                LowStatistics = lowStatistics,
                Reason = reason
            });
        }

        private static IReadOnlyList<double[]> BuildCoordinates(LocalizationPrediction pred)
        {
            if (pred.IsOutOfDistribution || pred.PredictedVector is null)
            {
                return Array.Empty<double[]>();
            }

            if (IsDualLabel(pred.Label) && pred.PredictedVector.Length >= 6)
            {
                return new[]
                {
                    new[] { pred.PredictedVector[0], pred.PredictedVector[1], pred.PredictedVector[2] },
                    new[] { pred.PredictedVector[3], pred.PredictedVector[4], pred.PredictedVector[5] }
                };
            }

            if (pred.PredictedVector.Length >= 3)
            {
                return new[]
                {
                    new[] { pred.PredictedVector[0], pred.PredictedVector[1], pred.PredictedVector[2] }
                };
            }

            return Array.Empty<double[]>();
        }

        private static bool IsDualLabel(string label)
        {
            return label.StartsWith("Dual", StringComparison.OrdinalIgnoreCase);
        }

        private void UpdateStabilityTracking(LocalizationPrediction pred)
        {
            bool isDual = IsDualLabel(pred.Label);
            if (pred.PredictedVector is null)
            {
                ResetStabilityTracking();
                return;
            }

            int required = isDual ? 6 : 3;
            if (pred.PredictedVector.Length < required)
            {
                ResetStabilityTracking();
                return;
            }

            if (_lastPrediction is null || _lastPredictionIsDual != isDual)
            {
                _stableCount = 0;
                _lastPrediction = CopyVector(pred.PredictedVector);
                _lastPredictionIsDual = isDual;
                _lastDelta = double.NaN;
                return;
            }

            double delta = isDual
                ? ComputeDualDelta(_lastPrediction, pred.PredictedVector)
                : ComputeSingleDelta(_lastPrediction, pred.PredictedVector);

            _stableCount = delta < _config.StabilityToleranceCm ? _stableCount + 1 : 0;
            _lastDelta = delta;
            _lastPrediction = CopyVector(pred.PredictedVector);
            _lastPredictionIsDual = isDual;
        }

        private void ResetStabilityTracking()
        {
            _stableCount = 0;
            _lastPrediction = null;
            _lastPredictionIsDual = null;
            _lastDelta = double.NaN;
        }

        private void UpdateLastEvaluation(LocalizationRequest req, LocalizationPrediction pred, double totalCounts)
        {
            LastEvaluation = new LocalizationEvaluation
            {
                EpisodeId = req.EpisodeId,
                IsProbe = req.IsProbe,
                DurationSeconds = req.Row.DurationSeconds,
                TotalCounts = totalCounts,
                Label = pred.Label,
                Probability = pred.ClassifierProbability,
                IsOod = pred.IsOutOfDistribution,
                MahalanobisDistance = pred.MahalanobisDistance,
                Delta = _lastDelta,
                StableCount = _stableCount
            };
        }

        private bool ShouldPublish(LocalizationPrediction pred)
        {
            bool earlyStop = _stableCount >= _config.EarlyStopK
                && IsProbabilityConfident(pred.ClassifierProbability, _config.EarlyStopProbability);
            bool stablePublish = _stableCount >= _config.StabilityK
                && IsProbabilityConfident(pred.ClassifierProbability, _config.PublishProbabilityMin);
            return earlyStop || stablePublish;
        }

        private static bool IsProbabilityConfident(double probability, double threshold)
        {
            return probability >= threshold || probability <= 1.0 - threshold;
        }

        private static double GetTotalCounts(IReadOnlyList<double> channels)
        {
            double total = 0;
            foreach (var value in channels)
            {
                total += value;
            }

            return total;
        }

        private static double[] CopyVector(double[] values)
        {
            var copy = new double[values.Length];
            Array.Copy(values, copy, values.Length);
            return copy;
        }

        private static double ComputeSingleDelta(double[] previous, double[] current)
        {
            return Distance(previous[0], previous[1], previous[2], current[0], current[1], current[2]);
        }

        private static double ComputeDualDelta(double[] previous, double[] current)
        {
            double prevAx = previous[0];
            double prevAy = previous[1];
            double prevAz = previous[2];
            double prevBx = previous[3];
            double prevBy = previous[4];
            double prevBz = previous[5];

            double currAx = current[0];
            double currAy = current[1];
            double currAz = current[2];
            double currBx = current[3];
            double currBy = current[4];
            double currBz = current[5];

            double distanceAA = Distance(prevAx, prevAy, prevAz, currAx, currAy, currAz);
            double distanceBB = Distance(prevBx, prevBy, prevBz, currBx, currBy, currBz);
            double distanceAB = Distance(prevAx, prevAy, prevAz, currBx, currBy, currBz);
            double distanceBA = Distance(prevBx, prevBy, prevBz, currAx, currAy, currAz);

            // Choose the ordering with the smaller total distance, then use the max matched distance as delta.
            if (distanceAB + distanceBA < distanceAA + distanceBB)
            {
                return Math.Max(distanceAB, distanceBA);
            }

            return Math.Max(distanceAA, distanceBB);
        }

        private static double Distance(double ax, double ay, double az, double bx, double by, double bz)
        {
            double dx = ax - bx;
            double dy = ay - by;
            double dz = az - bz;
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }
    }
}
