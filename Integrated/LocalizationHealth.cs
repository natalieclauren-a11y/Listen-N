using System;

namespace Integrated.Runtime
{
    public enum LocalizationHealthState
    {
        Healthy,
        Degraded,
        Unavailable,
        Disabled
    }

    public sealed class LocalizationHealthTracker
    {
        private readonly object _lock = new();
        private readonly int _refusalThreshold;
        private readonly int _queueSaturationThreshold;
        private int _refusalCount;
        private int _queueSaturationCount;
        private LocalizationHealthState _state;

        public LocalizationHealthTracker(int refusalThreshold, int queueSaturationThreshold)
        {
            _refusalThreshold = Math.Max(1, refusalThreshold);
            _queueSaturationThreshold = Math.Max(1, queueSaturationThreshold);
            _state = LocalizationHealthState.Healthy;
        }

        public LocalizationHealthState State
        {
            get
            {
                lock (_lock)
                {
                    return _state;
                }
            }
        }

        public void MarkDisabled()
        {
            lock (_lock)
            {
                _state = LocalizationHealthState.Disabled;
            }
        }

        public void MarkUnavailable()
        {
            lock (_lock)
            {
                if (_state == LocalizationHealthState.Disabled)
                {
                    return;
                }

                _state = LocalizationHealthState.Unavailable;
            }
        }

        public void RecordDecision(Integrated.Contracts.LocalizationDecisionRecord record)
        {
            lock (_lock)
            {
                if (_state == LocalizationHealthState.Disabled || _state == LocalizationHealthState.Unavailable)
                {
                    return;
                }

                if (record.Result.Outcome == Integrated.Contracts.LocalizationDecisionOutcome.Published)
                {
                    _refusalCount = 0;
                    _queueSaturationCount = 0;
                    _state = LocalizationHealthState.Healthy;
                    return;
                }

                if (record.ReasonCode == Integrated.Contracts.LocalizationDecisionReasonCode.Refused_ArtifactsNotLoaded)
                {
                    _state = LocalizationHealthState.Unavailable;
                    return;
                }

                if (record.ReasonCode == Integrated.Contracts.LocalizationDecisionReasonCode.Refused_MlException)
                {
                    _state = LocalizationHealthState.Degraded;
                    return;
                }

                if (record.Result.Outcome == Integrated.Contracts.LocalizationDecisionOutcome.Refused)
                {
                    _refusalCount++;
                }

                EvaluateDegraded();
            }
        }

        public void RecordQueueSaturation()
        {
            lock (_lock)
            {
                if (_state == LocalizationHealthState.Disabled || _state == LocalizationHealthState.Unavailable)
                {
                    return;
                }

                _queueSaturationCount++;
                EvaluateDegraded();
            }
        }

        public void RecordBackpressure()
        {
            RecordQueueSaturation();
        }

        private void EvaluateDegraded()
        {
            if (_state == LocalizationHealthState.Disabled || _state == LocalizationHealthState.Unavailable)
            {
                return;
            }

            if (_refusalCount >= _refusalThreshold || _queueSaturationCount >= _queueSaturationThreshold)
            {
                _state = LocalizationHealthState.Degraded;
            }
            else
            {
                _state = LocalizationHealthState.Healthy;
            }
        }
    }
}
