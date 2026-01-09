using System;
using System.Collections.Generic;
using Integrated.Contracts;
using RtLocalizationRequest = Integrated.Runtime.LocalizationRequest;

namespace Integrated.Runtime
{
    public sealed class LocalizationMlRequestLimiter
    {
        private readonly LocalizationWorker _worker;
        private readonly LocalizationEpisodePolicy _policy;
        private readonly LocalizationRuntimeConfig _config;
        private readonly LocalizationHealthTracker _health;
        private readonly object _lock = new();
        private readonly Dictionary<Guid, int> _episodeRequestCounts = new();

        public LocalizationMlRequestLimiter(
            LocalizationWorker worker,
            LocalizationEpisodePolicy policy,
            LocalizationRuntimeConfig config,
            LocalizationHealthTracker health)
        {
            _worker = worker ?? throw new ArgumentNullException(nameof(worker));
            _policy = policy ?? throw new ArgumentNullException(nameof(policy));
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _health = health ?? throw new ArgumentNullException(nameof(health));
        }

        public void HandleRequest(RtLocalizationRequest request)
        {
            if (!TryTrackEpisodeRequest(request.EpisodeId))
            {
                _policy.EmitBackpressureDecision(
                    LocalizationDecisionReasonCode.Deferred_Backpressure,
                    LocalizationDecisionOutcome.Deferred);
                _health.RecordBackpressure();
                return;
            }

            if (_worker.TryEnqueue(request, out var result))
            {
                return;
            }

            if (result == LocalizationWorkerEnqueueResult.QueueSaturated)
            {
                _policy.EmitBackpressureDecision(
                    LocalizationDecisionReasonCode.Refused_QueueSaturated,
                    LocalizationDecisionOutcome.Refused);
                _health.RecordQueueSaturation();
                return;
            }

            if (result == LocalizationWorkerEnqueueResult.SuppressedCooldown)
            {
                _policy.EmitBackpressureDecision(
                    LocalizationDecisionReasonCode.Deferred_Backpressure,
                    LocalizationDecisionOutcome.Deferred);
                _health.RecordBackpressure();
            }
        }

        public void ObserveDecision(LocalizationDecisionRecord record)
        {
            lock (_lock)
            {
                if (record.ReasonCode is LocalizationDecisionReasonCode.EpisodeStarted_ConfusionDebounced
                    or LocalizationDecisionReasonCode.EpisodeStarted_Manual)
                {
                    _episodeRequestCounts[record.EpisodeId] = 0;
                    return;
                }

                if (record.DecisionKind == LocalizationDecisionKind.EpisodeEnd)
                {
                    _episodeRequestCounts.Remove(record.EpisodeId);
                }
            }
        }

        private bool TryTrackEpisodeRequest(Guid episodeId)
        {
            lock (_lock)
            {
                if (!_episodeRequestCounts.TryGetValue(episodeId, out var count))
                {
                    _episodeRequestCounts[episodeId] = 1;
                    return true;
                }

                if (count >= _config.MaxMlRequestsPerEpisode)
                {
                    return false;
                }

                _episodeRequestCounts[episodeId] = count + 1;
                return true;
            }
        }
    }
}
