using System;
using System.Collections.Generic;
using System.Threading;
using Integrated.Contracts;
using RtLocalizationPrediction = Integrated.Runtime.LocalizationPrediction;
using RtLocalizationRequest = Integrated.Runtime.LocalizationRequest;

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
        public bool IsManual { get; init; }
        public long Sequence { get; init; } = DateTimeOffset.UtcNow.UtcTicks;
        public CancellationToken CancellationToken { get; init; } = CancellationToken.None;
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
        public required double[]? PredictedVector { get; init; }
        public string? Reason { get; init; }
        public string? ModelId { get; init; }
        public double? InferenceTimeMs { get; init; }
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
        public int Nmin_15cm_30s { get; init; }
        public int Nmin_15cm_60s { get; init; }

        public static TriggerPolicyThresholds Defaults => new()
        {
            Nmin_15cm_30s = 20000,
            Nmin_15cm_60s = 40000
        };

        public int GetThreshold(double durationSeconds, double minDurationSeconds, double maxDurationSeconds)
        {
            if (maxDurationSeconds <= minDurationSeconds)
            {
                return Nmin_15cm_60s;
            }

            if (durationSeconds <= minDurationSeconds)
            {
                return Nmin_15cm_30s;
            }

            if (durationSeconds >= maxDurationSeconds)
            {
                return Nmin_15cm_60s;
            }

            double ratio = (durationSeconds - minDurationSeconds) / (maxDurationSeconds - minDurationSeconds);
            double threshold = Nmin_15cm_30s + (Nmin_15cm_60s - Nmin_15cm_30s) * ratio;
            return (int)Math.Round(threshold);
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
        private int _stableCount;
        private double[]? _lastPrediction;
        private bool? _lastPredictionIsDual;
        private RtWindowSummary? _lastWindowSummary;
        private bool _lastIsConfused;
        private double _lastDelta = double.NaN;
        private long _requestSequence;
        private LocalizationDecisionRecord? _latestDecisionRecord;

        public LocalizationEpisodePolicy(LocalizationEpisodePolicyConfig config, TriggerPolicyThresholds thresholds)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _thresholds = thresholds;
            _stateHistory = new List<string>(_config.ThrashWindowCount);
        }

        public event Action<RtLocalizationRequest>? OnRequestMl;
        public event Action<LocalizationStatus>? OnStatus;
        public event Action<LocalizationPublishResult>? OnPublish;
        public event Action<LocalizationDecisionRecord>? OnDecisionRecord;

        public bool AutoModeEnabled { get; set; } = true;

        public bool IsEpisodeActive => _episodeActive;
        public double AccumulatedCountsTotal => _accumTotalCounts;
        public double AccumulatedDurationSeconds => _accumDuration;
        public bool LastIsConfused => _lastIsConfused;
        public double LastStabilityDelta => _lastDelta;
        public int StableCount => _stableCount;

        public LocalizationEvaluation? LastEvaluation { get; private set; }
        public LocalizationDecisionRecord? LatestDecisionRecord => _latestDecisionRecord;
        public Guid RunId { get; set; }

        public void AddWindow(RtWindowSummary w)
        {
            _lastWindowSummary = w;
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
                EmitDecision(BuildDecisionRecord(
                    LocalizationDecisionKind.TriggerEvaluated,
                    LocalizationDecisionReasonCode.Deferred_AutoDisabled,
                    LocalizationDecisionOutcome.Deferred,
                    window: w,
                    request: null,
                    prediction: null));
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
                    else
                    {
                        EmitDecision(BuildDecisionRecord(
                            LocalizationDecisionKind.TriggerEvaluated,
                            LocalizationDecisionReasonCode.Deferred_WaitingForConfusionDebounce,
                            LocalizationDecisionOutcome.Deferred,
                            window: w,
                            request: null,
                            prediction: null));
                    }
                }
                else
                {
                    _confusedStreak = 0;
                    _pendingDuration = 0;
                    _pendingTotalCounts = 0;
                    EmitDecision(BuildDecisionRecord(
                        LocalizationDecisionKind.TriggerEvaluated,
                        LocalizationDecisionReasonCode.Deferred_NotConfused,
                        LocalizationDecisionOutcome.Deferred,
                        window: w,
                        request: null,
                        prediction: null));
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
                        EmitDecision(BuildDecisionRecord(
                            LocalizationDecisionKind.EpisodeEnd,
                            LocalizationDecisionReasonCode.EpisodeEnded_ConfusionCleared,
                            LocalizationDecisionOutcome.None,
                            window: w,
                            request: null,
                            prediction: null));
                        ResetEpisode();
                        return;
                    }
                }

                ScheduleRequestsIfNeeded();
            }
        }

        public void OnMlResult(RtLocalizationRequest req, RtLocalizationPrediction pred)
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
            var threshold = GetCountThreshold(duration);
            bool withinPublishWindow = duration >= _config.MinPublishDurationSeconds && duration <= _config.MaxPublishDurationSeconds;
            bool countsGateMet = threshold > 0 && totalCounts >= threshold;
            bool durationGateMet = duration >= _config.MinPublishDurationSeconds;
            bool oodGatePassed = !pred.IsOutOfDistribution;
            bool stabilityGatePassed = _stableCount >= _config.StabilityK;
            bool probabilityGatePassed = IsProbabilityConfident(pred.ClassifierProbability, _config.PublishProbabilityMin);
            bool hasValidShape = HasValidPredictionShape(pred);
            if (!string.IsNullOrWhiteSpace(pred.Reason))
            {
                ResetStabilityTracking();
                UpdateLastEvaluation(req, pred, totalCounts);
                EmitDecision(BuildDecisionRecord(
                    LocalizationDecisionKind.Refuse,
                    MapRefusalReason(pred.Reason),
                    LocalizationDecisionOutcome.Refused,
                    window: _lastWindowSummary,
                    request: req,
                    prediction: pred,
                    threshold,
                    countsGateMet,
                    durationGateMet,
                    oodGatePassed,
                    stabilityGatePassed,
                    probabilityGatePassed,
                    lowStatistics: false,
                    publishedCoords: null));
                return;
            }

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
                EmitDecision(BuildDecisionRecord(
                    LocalizationDecisionKind.Publish,
                    GetFinalPublishReason(pred, countsGateMet),
                    LocalizationDecisionOutcome.Published,
                    window: _lastWindowSummary,
                    request: req,
                    prediction: pred,
                    threshold,
                    countsGateMet,
                    durationGateMet,
                    oodGatePassed,
                    stabilityGatePassed,
                    probabilityGatePassed,
                    lowStatistics: !countsGateMet,
                    publishedCoords: BuildCoordinates(pred)));
                EmitDecision(BuildDecisionRecord(
                    LocalizationDecisionKind.EpisodeEnd,
                    LocalizationDecisionReasonCode.EpisodeEnded_Finalized,
                    LocalizationDecisionOutcome.None,
                    window: _lastWindowSummary,
                    request: req,
                    prediction: pred));
                ResetEpisode();
                return;
            }

            if (!withinPublishWindow || pred.IsOutOfDistribution)
            {
                ResetStabilityTracking();
                UpdateLastEvaluation(req, pred, totalCounts);
                EmitDecision(BuildDecisionRecord(
                    pred.IsOutOfDistribution ? LocalizationDecisionKind.Refuse : LocalizationDecisionKind.Defer,
                    pred.IsOutOfDistribution
                        ? LocalizationDecisionReasonCode.Refused_OOD
                        : LocalizationDecisionReasonCode.Deferred_WaitingForDurationMin,
                    pred.IsOutOfDistribution ? LocalizationDecisionOutcome.Refused : LocalizationDecisionOutcome.Deferred,
                    window: _lastWindowSummary,
                    request: req,
                    prediction: pred,
                    threshold,
                    countsGateMet,
                    durationGateMet,
                    oodGatePassed,
                    stabilityGatePassed,
                    probabilityGatePassed,
                    lowStatistics: !countsGateMet,
                    publishedCoords: null));
                return;
            }

            UpdateStabilityTracking(pred);
            stabilityGatePassed = _stableCount >= _config.StabilityK;
            probabilityGatePassed = IsProbabilityConfident(pred.ClassifierProbability, _config.PublishProbabilityMin);
            UpdateLastEvaluation(req, pred, totalCounts);
            if (!_publishCandidate && !countsGateMet)
            {
                EmitDecision(BuildDecisionRecord(
                    LocalizationDecisionKind.Defer,
                    LocalizationDecisionReasonCode.Deferred_WaitingForCounts,
                    LocalizationDecisionOutcome.Deferred,
                    window: _lastWindowSummary,
                    request: req,
                    prediction: pred,
                    threshold,
                    countsGateMet,
                    durationGateMet,
                    oodGatePassed,
                    stabilityGatePassed,
                    probabilityGatePassed,
                    lowStatistics: true,
                    publishedCoords: null));
                return;
            }

            if (ShouldPublish(pred))
            {
                EmitPublishResult(req, pred, totalCounts, lowStatistics: false, reason: null);
                EmitDecision(BuildDecisionRecord(
                    LocalizationDecisionKind.Publish,
                    GetPublishReason(pred),
                    LocalizationDecisionOutcome.Published,
                    window: _lastWindowSummary,
                    request: req,
                    prediction: pred,
                    threshold,
                    countsGateMet,
                    durationGateMet,
                    oodGatePassed,
                    stabilityGatePassed,
                    probabilityGatePassed,
                    lowStatistics: false,
                    publishedCoords: BuildCoordinates(pred)));
                EmitDecision(BuildDecisionRecord(
                    LocalizationDecisionKind.EpisodeEnd,
                    LocalizationDecisionReasonCode.EpisodeEnded_Published,
                    LocalizationDecisionOutcome.None,
                    window: _lastWindowSummary,
                    request: req,
                    prediction: pred));
                ResetEpisode();
                return;
            }
            if (!hasValidShape)
            {
                EmitDecision(BuildDecisionRecord(
                    LocalizationDecisionKind.Refuse,
                    LocalizationDecisionReasonCode.Refused_InvalidPredictionShape,
                    LocalizationDecisionOutcome.Refused,
                    window: _lastWindowSummary,
                    request: req,
                    prediction: pred,
                    threshold,
                    countsGateMet,
                    durationGateMet,
                    oodGatePassed,
                    stabilityGatePassed,
                    probabilityGatePassed,
                    lowStatistics: false,
                    publishedCoords: null));
                return;
            }

            EmitDecision(BuildDecisionRecord(
                LocalizationDecisionKind.Defer,
                stabilityGatePassed
                    ? LocalizationDecisionReasonCode.Deferred_WaitingForProbability
                    : LocalizationDecisionReasonCode.Deferred_WaitingForStability,
                LocalizationDecisionOutcome.Deferred,
                window: _lastWindowSummary,
                request: req,
                prediction: pred,
                threshold,
                countsGateMet,
                durationGateMet,
                oodGatePassed,
                stabilityGatePassed,
                probabilityGatePassed,
                lowStatistics: false,
                publishedCoords: null));

        }

        private int GetCountThreshold(double durationSeconds)
        {
            return _thresholds.GetThreshold(durationSeconds, _config.MinPublishDurationSeconds, _config.MaxPublishDurationSeconds);
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

        internal RtLocalizationRequest BuildManualProbeRequest()
        {
            if (!_episodeActive)
            {
                StartManualEpisode(DateTimeOffset.UtcNow, _lastWindowSummary);
            }

            var request = BuildRequest(isProbe: true, isManual: true);
            _probePending = true;
            _countsAtLastRequest = _accumTotalCounts;
            EmitStatus(LocalizationStatusCodes.ProbeRequested, _episodeId, "Manual probe requested.");
            return request;
        }

        private RtLocalizationRequest BuildRequest(bool isProbe, bool isManual = false)
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
                IsManual = isManual,
                Sequence = Interlocked.Increment(ref _requestSequence),
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
            EmitDecision(BuildDecisionRecord(
                LocalizationDecisionKind.EpisodeStart,
                LocalizationDecisionReasonCode.EpisodeStarted_ConfusionDebounced,
                LocalizationDecisionOutcome.None,
                window: _lastWindowSummary,
                request: null,
                prediction: null));
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
            EmitDecision(BuildDecisionRecord(
                LocalizationDecisionKind.EpisodeStart,
                LocalizationDecisionReasonCode.EpisodeStarted_Manual,
                LocalizationDecisionOutcome.None,
                window: seedWindow ?? _lastWindowSummary,
                request: null,
                prediction: null));
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

        private void PublishFinalResult(RtLocalizationRequest req, RtLocalizationPrediction pred, double totalCounts, bool countsGateMet)
        {
            if (pred.IsOutOfDistribution)
            {
                EmitPublishResult(req, pred with { Label = "Unknown" }, totalCounts, lowStatistics: false, reason: "OOD at max duration");
                return;
            }

            bool lowStatistics = !countsGateMet;
            EmitPublishResult(req, pred, totalCounts, lowStatistics, reason: null);
        }

        private void EmitPublishResult(RtLocalizationRequest req, RtLocalizationPrediction pred, double totalCounts, bool lowStatistics, string? reason)
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

        private static IReadOnlyList<double[]> BuildCoordinates(RtLocalizationPrediction pred)
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

        private bool HasValidPredictionShape(RtLocalizationPrediction pred)
        {
            if (pred.PredictedVector is null)
            {
                return false;
            }

            int required = IsDualLabel(pred.Label) ? 6 : 3;
            return pred.PredictedVector.Length >= required;
        }

        private LocalizationDecisionRecord BuildDecisionRecord(
            LocalizationDecisionKind kind,
            LocalizationDecisionReasonCode reason,
            LocalizationDecisionOutcome outcome,
            RtWindowSummary? window,
            RtLocalizationRequest? request,
            RtLocalizationPrediction? prediction,
            int? activeThreshold = null,
            bool countsGatePassed = false,
            bool durationGatePassed = false,
            bool oodGatePassed = false,
            bool stabilityGatePassed = false,
            bool probabilityGatePassed = false,
            bool lowStatistics = false,
            IReadOnlyList<double[]>? publishedCoords = null)
        {
            var rtContext = BuildRtContext(window, request);
            var episodeContext = BuildEpisodeContext();
            var policyContext = BuildPolicyContext(activeThreshold, countsGatePassed, durationGatePassed, oodGatePassed, stabilityGatePassed, probabilityGatePassed);
            var mlContext = BuildMlContext(prediction);
            var result = new LocalizationDecisionResult
            {
                Outcome = outcome,
                LowStatistics = lowStatistics,
                PublishedCoords = publishedCoords is null ? null : FlattenCoordinates(publishedCoords)
            };

            return new LocalizationDecisionRecord
            {
                RunId = RunId,
                TimestampUtc = DateTime.UtcNow,
                DecisionKind = kind,
                ReasonCode = reason,
                EpisodeId = _episodeId,
                EpisodeActive = _episodeActive,
                RtContext = rtContext,
                EpisodeContext = episodeContext,
                PolicyContext = policyContext,
                MlContext = mlContext,
                Result = result
            };
        }

        public void EmitBackpressureDecision(LocalizationDecisionReasonCode reason, LocalizationDecisionOutcome outcome)
        {
            var kind = outcome == LocalizationDecisionOutcome.Deferred
                ? LocalizationDecisionKind.Defer
                : LocalizationDecisionKind.Refuse;

            EmitDecision(BuildDecisionRecord(
                kind,
                reason,
                outcome,
                window: _lastWindowSummary,
                request: null,
                prediction: null));
        }

        private LocalizationDecisionRtContext BuildRtContext(RtWindowSummary? window, RtLocalizationRequest? request)
        {
            if (window is null)
            {
                return new LocalizationDecisionRtContext
                {
                    WindowDurationSeconds = request?.Row.DurationSeconds ?? 0,
                    WindowTotalCounts = request is null ? 0 : (int)Math.Round(GetTotalCounts(request.Row.Channels)),
                    RtStateName = string.Empty,
                    ConfusionMetric = double.NaN,
                    IsConfusedCandidate = false,
                    QualityScalar = double.NaN,
                    RateTotalCps = double.NaN
                };
            }

            return new LocalizationDecisionRtContext
            {
                WindowDurationSeconds = window.DurationSeconds,
                WindowTotalCounts = (int)Math.Round(GetTotalCounts(window.Counts15)),
                RtStateName = window.RtState,
                ConfusionMetric = window.QualityScalar,
                IsConfusedCandidate = window.IsConfusedCandidate,
                QualityScalar = window.QualityScalar,
                RateTotalCps = window.RateTotalCps
            };
        }

        private LocalizationDecisionEpisodeContext BuildEpisodeContext()
        {
            return new LocalizationDecisionEpisodeContext
            {
                AccumulatedDurationSeconds = _accumDuration,
                AccumulatedTotalCounts = _accumTotalCounts,
                ConfusionDebounceCount = _confusedStreak,
                RecoveryDebounceCount = _recoveryStreak,
                StabilityDeltaCm = _lastDelta,
                ConsecutiveStableCount = _stableCount,
                RequiredStableCount = _config.StabilityK
            };
        }

        private LocalizationDecisionPolicyContext BuildPolicyContext(
            int? activeThreshold,
            bool countsGatePassed,
            bool durationGatePassed,
            bool oodGatePassed,
            bool stabilityGatePassed,
            bool probabilityGatePassed)
        {
            var gatingReasons = new List<string>();
            if (!countsGatePassed)
            {
                gatingReasons.Add(nameof(LocalizationDecisionReasonCode.Deferred_WaitingForCounts));
            }

            if (!durationGatePassed)
            {
                gatingReasons.Add(nameof(LocalizationDecisionReasonCode.Deferred_WaitingForDurationMin));
            }

            if (!oodGatePassed)
            {
                gatingReasons.Add(nameof(LocalizationDecisionReasonCode.Refused_OOD));
            }

            if (!stabilityGatePassed)
            {
                gatingReasons.Add(nameof(LocalizationDecisionReasonCode.Deferred_WaitingForStability));
            }

            if (!probabilityGatePassed)
            {
                gatingReasons.Add(nameof(LocalizationDecisionReasonCode.Deferred_WaitingForProbability));
            }

            return new LocalizationDecisionPolicyContext
            {
                MinPublishDurationSeconds = _config.MinPublishDurationSeconds,
                MaxPublishDurationSeconds = _config.MaxPublishDurationSeconds,
                Nmin15cm_Single = _thresholds.Nmin_15cm_30s,
                Nmin15cm_Dual = _thresholds.Nmin_15cm_60s,
                ActiveThresholdUsed = activeThreshold ?? 0,
                ActiveThresholdMode = _lastPredictionIsDual == true ? "Dual" : "Single",
                CountsGatePassed = countsGatePassed,
                DurationGatePassed = durationGatePassed,
                OodGatePassed = oodGatePassed,
                StabilityGatePassed = stabilityGatePassed,
                ProbabilityGatePassed = probabilityGatePassed,
                GatingReasons = gatingReasons
            };
        }

        private static LocalizationDecisionMlContext? BuildMlContext(RtLocalizationPrediction? prediction)
        {
            if (prediction is null)
            {
                return null;
            }

            return new LocalizationDecisionMlContext
            {
                ModelId = prediction.ModelId,
                InferenceTimeMs = prediction.InferenceTimeMs,
                OutcomeLabel = prediction.Label,
                ClassifierProbability = prediction.ClassifierProbability,
                MahalanobisDistance = prediction.MahalanobisDistance,
                IsOutOfDistribution = prediction.IsOutOfDistribution,
                PredictedCoords = prediction.PredictedVector
            };
        }

        private static double[]? FlattenCoordinates(IReadOnlyList<double[]> coords)
        {
            if (coords.Count == 0)
            {
                return null;
            }

            var flattened = new List<double>();
            foreach (var coord in coords)
            {
                if (coord is null)
                {
                    continue;
                }

                flattened.AddRange(coord);
            }

            return flattened.Count == 0 ? null : flattened.ToArray();
        }

        private void EmitDecision(LocalizationDecisionRecord record)
        {
            _latestDecisionRecord = record;
            OnDecisionRecord?.Invoke(record);
        }

        private LocalizationDecisionReasonCode MapRefusalReason(string reason)
        {
            return reason switch
            {
                "Timeout" => LocalizationDecisionReasonCode.Refused_MlTimeout,
                "Cancelled" => LocalizationDecisionReasonCode.Refused_Cancelled,
                "ArtifactsNotLoaded" => LocalizationDecisionReasonCode.Refused_ArtifactsNotLoaded,
                "MlException" => LocalizationDecisionReasonCode.Refused_MlException,
                _ => LocalizationDecisionReasonCode.Refused_Unknown
            };
        }

        private LocalizationDecisionReasonCode GetFinalPublishReason(RtLocalizationPrediction pred, bool countsGateMet)
        {
            if (pred.IsOutOfDistribution)
            {
                return LocalizationDecisionReasonCode.Published_OodAtMaxDuration;
            }

            if (!countsGateMet)
            {
                return LocalizationDecisionReasonCode.Published_LowStatisticsAtMaxDuration;
            }

            return GetPublishReason(pred);
        }

        private LocalizationDecisionReasonCode GetPublishReason(RtLocalizationPrediction pred)
        {
            bool earlyStop = _stableCount >= _config.EarlyStopK
                && IsProbabilityConfident(pred.ClassifierProbability, _config.EarlyStopProbability);
            if (earlyStop)
            {
                return LocalizationDecisionReasonCode.Published_EarlyStop;
            }

            return IsDualLabel(pred.Label)
                ? LocalizationDecisionReasonCode.Published_Dual
                : LocalizationDecisionReasonCode.Published_Single;
        }

        private void UpdateStabilityTracking(RtLocalizationPrediction pred)
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

        private void UpdateLastEvaluation(RtLocalizationRequest req, RtLocalizationPrediction pred, double totalCounts)
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

        private bool ShouldPublish(RtLocalizationPrediction pred)
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
