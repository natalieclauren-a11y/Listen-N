// AdaptiveWindowEngine.cs
// Purpose: Implements an adaptive sliding-window engine for real-time neutron noise analysis.
// Scope: Ingests detection events, maintains a circular buffer of counts, computes factorial moments,
//        estimates the Feynman-Y statistic, and adapts gate/window size to meet uncertainty targets.
// Assumptions & Units:
//   - Time values are microseconds (µs). Window sizes are in seconds.
//   - Detection events carry timestamps and detector IDs.
//   - Error propagation uses linearized approximations.
// Error handling:
//   - Guard checks against invalid denominators or non-physical values.
// Threading:
//   - Background worker thread drains channel of incoming detections.
//   - Estimate events are fired asynchronously.
// Numerical stability:
//   - Uses Page–Hinkley test for transient detection, with safeguards for drifts.

namespace Listen_N
{
    using System;
    using System.Linq;
    using System.Threading;
    using System.Threading.Channels;
    using Integrated.Contracts;

    // Struct representing a single detection event with timestamp and detector ID
    public readonly struct Detection
    {
        public readonly long TicksUs;   // detection time (µs)
        public readonly byte DetectorId; // detector channel ID

        public Detection(long ticksUs, byte detId = 0)
        {
            TicksUs = ticksUs;
            DetectorId = detId;
        }
    }

    // Adaptive sliding window engine for real-time neutron noise analysis
    public sealed class AdaptiveWindowEngine : IDisposable
    {
        // Encapsulates one statistical estimate output by the engine
        public sealed class Estimate
        {
            public long NowUs { get; init; }       // current time in µs
            public int GateUs { get; init; }       // gate width in µs
            public double WindowSec { get; init; } // current analysis window size (s)
            public double M1 { get; init; }        // first factorial moment
            public double M2 { get; init; }        // second factorial moment
            public double M3 { get; init; }        // third factorial moment
            public double VarM1 { get; init; }     // variance of m1_hat
            public double VarM2 { get; init; }     // variance of m2_hat
            public double VarM3 { get; init; }     // variance of m3_hat
            public double CovM1M2 { get; init; }   // covariance of m1_hat and m2_hat
            public double CovM1M3 { get; init; }   // covariance of m1_hat and m3_hat
            public double CovM2M3 { get; init; }   // covariance of m2_hat and m3_hat
            public double Y { get; init; }         // Feynman-Y statistic
            public double SigmaY { get; init; }    // uncertainty of Y
            public double ZY { get; init; }        // Z-score of Y (Y / σY)
            public string State { get; init; } = "Track"; // finite state machine state
            public bool HasSignificance { get; init; }     // significance flag
            public bool IsLowRate { get; init; }
            public bool IsDegraded { get; init; }
            public bool IsStatsBound { get; init; }
            public bool InsufficientStatistics { get; init; }
            public bool ModelMismatch { get; init; }
            public double MaxAbsZ { get; init; }
            public int PoissonQuietStreak { get; init; }
        }

        public event Action<Estimate>? OnEstimate; // callback for new estimates
        public event Action<RtWindowSummary>? OnWindowSummary; // callback for per-window summaries

        private const int DetectorCount = 15;

        // ——— configuration ———
        private readonly int[] _tgUs;   // available gate widths (µs)
        private readonly int _deltaUs;  // base bin width (µs)
        private readonly double _wMin;  // min window size (s)
        private readonly double _wMax;  // max window size (s)
        private int _zMin = 3;          // minimum significance threshold
        private int _minGateCountForZ;  // minimum gate count to include in max |Z|

        public int Zmin
        {
            get => _zMin;
            set => _zMin = Math.Max(1, value);
        }

        public double ZTrack
        {
            get => _zTrack;
            set
            {
                _zTrack = Math.Max(0, value);
                _zHold = Math.Max(_zHold, _zTrack);
            }
        }

        public double ZHold
        {
            get => _zHold;
            set => _zHold = Math.Max(_zTrack, value);
        }

        public double ZPoisson
        {
            get => _zPoisson;
            set => _zPoisson = Math.Max(0, value);
        }

        private double _epsY;     // target relative uncertainty for Y
        private double _epsM1;    // target relative uncertainty for M1
        private double _etaFrac = 0.05; // adaptation fraction

        public long LeftEdgeUs => _acc.LeftEdgeUs; // current left edge of accumulator window

        public double EtaFrac
        {
            get => _etaFrac;
            set => _etaFrac = Math.Max(0, value);
        }

        public int MinGateCountForZ
        {
            get => _minGateCountForZ;
            set => _minGateCountForZ = Math.Max(0, value);
        }

        public int DebugEventCount => _acc.TotalEvents;

        public int DebugBinsWithCounts => _acc.CountNonEmptyBins();

        // ——— runtime state ———
        private readonly BaseBinAccumulator _acc; // accumulator of counts
        private readonly Channel<Detection> _inbound; // inbound channel
        private readonly Thread? _worker;             // worker thread
        private readonly bool _startWorker;
        private volatile bool _running = true;       // loop control flag
        private long _lastTimestampUs = 0;           // last processed detection timestamp
        private readonly double _initialWindowSec;

        private readonly struct MomentCovariance
        {
            public readonly double V11; // Var(m1_hat)
            public readonly double V22; // Var(m2_hat)
            public readonly double V33; // Var(m3_hat)
            public readonly double V12; // Cov(m1_hat, m2_hat)
            public readonly double V13; // Cov(m1_hat, m3_hat)
            public readonly double V23; // Cov(m2_hat, m3_hat)

            public MomentCovariance(double v11, double v22, double v33, double v12, double v13, double v23)
            {
                V11 = v11; V22 = v22; V33 = v33; V12 = v12; V13 = v13; V23 = v23;
            }
        }

        // Finite State Machine states for adaptation
        private enum FSM { Warmup, Poisson, Track, Expand, Contract, Hold, LowRate, Degraded }
        private FSM _fsm = FSM.Warmup;

        private int _pendingTgIdx = -1;
        private int _tgConfirmations = 0;

        private int _tgIdx;      // current gate index in ladder
        private bool _tgIsMilliseconds;
        private double _W;       // current window size (s)
        private double _beta;    // step fraction for sliding window
        private double _S;       // step size (s)

        private double _zTrack = 3.0;
        private double _zHold = 6.0;
        private double _zPoisson = 4.0;
        private readonly int _poissonQuietRequired = 1;
        private int _poissonQuietStreak = 1;

        private int _mismatchStreak;
        private int _mismatchClearStreak;
        private readonly int _mismatchStreakRequired = 3;
        private readonly int _mismatchClearRequired = 6;

        private double _tauHat = double.NaN;
        private double[] _corrResiduals = Array.Empty<double>();
        private bool _statsBound;
        private bool _insufficientStatistics;
        private bool _modelMismatch;

        private FSM _pendingFsm = FSM.Warmup;
        private int _fsmConfirmations = 0;
        private long _holdQuietUntilUs = 0;
        private long _nextStepUs = 0;

        // Page–Hinkley change-point detection variables
        private double _cpMean, _cpCum;
        private readonly double _cpDelta = 5e-3, _cpLambda = 50.0;
        private bool _phAlarm;
        private double _cpZyMean, _cpZyCum;

        public AdaptiveWindowEngine(
           int baseDeltaUs = 50,
           double windowStartSec = 0.5,
           double windowMinSec = 0.5,
           double windowMaxSec = 60.0,
           int[]? gateLadderUs = null,
           int zMin = 3,
           double epsY = 0.10,
           double epsM1 = 0.02,
           bool startWorker = true,
           int minGateCountForZ = 0,
           bool enableFileLog = true,
           string? logPath = "adaptive.log")
        {
            _tgUs = gateLadderUs ?? new[] { 500, 1000, 2000, 4000, 8000, 16000, 32000 };
            _tgIsMilliseconds = _tgUs.Length > 0 && _tgUs[0] < 100;
            _tgIdx = DefaultGateIndex();
            _wMin = windowMinSec;
            _wMax = windowMaxSec;
            _initialWindowSec = windowStartSec;
            _W = _initialWindowSec;
            _beta = BetaForState(_fsm);
            _zMin = zMin;
            _zTrack = Math.Max(1.0, zMin);
            _zHold = Math.Max(_zHold, _zTrack + 3.0);
            _epsY = epsY;
            _epsM1 = epsM1;
            _startWorker = startWorker;
            _minGateCountForZ = Math.Max(0, minGateCountForZ);

            _deltaUs = Math.Max(baseDeltaUs, Math.Max(10, GateWidthUs(0) / 10));
            _startWorker = startWorker;
            _enableFileLog = enableFileLog && startWorker;   // key line: tests (startWorker:false) will not log
            _logPath = logPath;
            // initialize accumulator and channel infrastructure
            _acc = new BaseBinAccumulator(_deltaUs, _wMax, DetectorCount);
            _inbound = Channel.CreateBounded<Detection>(new BoundedChannelOptions(1 << 16)
            {
                SingleWriter = false,
                SingleReader = true
            });
            if (_startWorker)
            {
                _worker = new Thread(Worker) { IsBackground = true, Name = "AdaptiveWindowEngine" };
                _worker.Start();
            }
        }

        // Add near other fields (class scope)
        private readonly bool _enableFileLog;
        private readonly string? _logPath;
        private static readonly object _logLock = new();

        // Add a small helper (class scope)
        private static void AppendLineShared(string path, string line)
        {
            lock (_logLock)
            {
                using var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                using var sw = new StreamWriter(fs);
                sw.WriteLine(line);
            }
        }


        public void Dispose()
        {
            _running = false;
            _worker?.Join();
        }

        // Push a new detection into channel (non-blocking)
        public void OnDetection(in Detection d)
        {
            _inbound.Writer.TryWrite(d);
        }

        // ——— worker loop ———
        private void Worker()
        {
            var r = _inbound.Reader;
            while (_running)
            {
                DrainInbound(r);
                long nowUs = _acc.LeftEdgeUs + (long)(_W * 1e6);
                ProcessSteps(nowUs, r, drainInbound: false);
                Thread.SpinWait(256);
            }
        }

        private void InitializeNextStep()
        {
            if (_nextStepUs != 0) return;

            // Schedule first analysis tick at the end of the current window,
            // even if LeftEdgeUs is 0 (valid when a scenario starts at t=0).
            _nextStepUs = _acc.LeftEdgeUs + (long)(_W * 1e6);

            // Ultra-defensive guard: if window is somehow zero, schedule the next tick at 1us.
            if (_nextStepUs == 0) _nextStepUs = 1;
        }

        private void DrainInbound(ChannelReader<Detection>? reader = null)
        {
            var r = reader ?? _inbound.Reader;
            while (r.TryRead(out var d))
            {
                _acc.Add(d.TicksUs, d.DetectorId);
                _lastTimestampUs = d.TicksUs;
            }
        }

        // Reset timestamp-related state for a new scenario
        public void ResetTimestampState(long firstTimestampUs)
        {
            while (_inbound.Reader.TryRead(out _)) { }

            _acc.ResetTo(firstTimestampUs);
            _lastTimestampUs = 0;
            _nextStepUs = 0;
            _fsm = FSM.Warmup;
            _pendingFsm = FSM.Warmup;
            _fsmConfirmations = 0;
            _holdQuietUntilUs = 0;

            _pendingTgIdx = -1;
            _tgConfirmations = 0;
            _tgIdx = DefaultGateIndex();

            _W = _initialWindowSec;
            _S = 0;
            _beta = BetaForState(_fsm);

            _poissonQuietStreak = 0;
            _tauHat = double.NaN;
            _corrResiduals = Array.Empty<double>();
            _statsBound = false;
            _insufficientStatistics = false;
            _modelMismatch = false;
            _mismatchStreak = 0;
            _mismatchClearStreak = 0;

            _cpMean = 0;
            _cpCum = 0;
            _cpZyMean = 0;
            _cpZyCum = 0;
        }

        private int DefaultGateIndex() => Math.Min(1, _tgUs.Length - 1);

        private void ProcessSteps(long nowUs, ChannelReader<Detection>? reader = null, bool drainInbound = true)
        {
            if (drainInbound)
            {
                DrainInbound(reader);
            }

            InitializeNextStep();
            if (_nextStepUs == 0) return;

            while (_nextStepUs != 0 && nowUs >= _nextStepUs)
            {
                Step(_nextStepUs);
            }
        }

        public void ForceEstimate(long nowUs)
        {
            ForceStep(nowUs);
        }

        public void ForceStep(long nowUs)
{
    // Make sure detections are drained (same as ProcessSteps would do)
    DrainInbound();

    InitializeNextStep();
    if (_nextStepUs == 0) return;

    // FORCE: if caller asks for an analysis time before the scheduled tick,
    // run analysis at nowUs anyway (this is what tests and diagnostics expect).
    if (nowUs < _nextStepUs)
    {
        Step(nowUs); // calls RunAnalysis + advances _nextStepUs
    }
    else
    {
        // Normal stepping behavior
        while (_nextStepUs != 0 && nowUs >= _nextStepUs)
        {
            Step(_nextStepUs);
        }
    }

    if (!_startWorker)
    {
        Console.WriteLine($"ForceStep: events={DebugEventCount}, bins_with_counts={DebugBinsWithCounts}");
    }
}


        // Compute adaptive step size for sliding window
        private double StepSizeSec()
        {
            _S = Math.Max(_deltaUs / 1e6, _beta * _W);
            _S = Math.Clamp(_S, 1e-3, _wMax);
            return _S;
        }

        private int GateWidthUs(int idx) => _tgIsMilliseconds ? _tgUs[idx] * 1000 : _tgUs[idx];

        // Perform one analysis step
        private void Step(long nowUs)
        {
            RunAnalysis(nowUs);
            _nextStepUs = nowUs + (long)(StepSizeSec() * 1e6);
        }

        private void RunAnalysis(long nowUs)
        {
            long leftEdgeUs = Math.Max(0, nowUs - (long)(_W * 1e6));
            _acc.SlideLeftTo(leftEdgeUs);

            _statsBound = false;
            _modelMismatch = false;
            _insufficientStatistics = false;

            double selM1 = 0, selM2 = 0, selM3 = 0;
            int selN = 0;
            double selY = 0, selSigY = 0;

            int significantIdx = -1;
            int plateauIdx = -1;

            double[] Yk = new double[_tgUs.Length];
            double[] sigYk = new double[_tgUs.Length];
            double maxAbsZ = 0;

            // covariance entries for selected gate
            double selV11 = 0, selV22 = 0, selV33 = 0, selV12 = 0, selV13 = 0, selV23 = 0;
            bool illConditioned = false;
            bool anyValidY = false;
            bool allNonPositiveY = true;

            // loop across ladder of gate widths
            for (int k = 0; k < _tgUs.Length; k++)
            {
                int gateUs = GateWidthUs(k);
                _acc.ComputeMoments(_W, gateUs,
                    out var m1, out var m2, out var m3, out var N,
                    out var rawCov);

                bool covOk = RegularizeCov(rawCov, out var cov);
                illConditioned |= !covOk;

                // compute Feynman-Y and a rough uncertainty estimate using delta method
                var y = MomentsMath.Y(m1, m2);
                Yk[k] = y;

                double sigY = double.PositiveInfinity;
                if (N > 1 && covOk)
                {
                    double varY = MomentsMath.VarY(m1, m2, cov.V11, cov.V22, cov.V12);
                    if (double.IsFinite(varY) && varY > 0)
                    {
                        sigY = Math.Sqrt(varY);
                    }
                    else
                    {
                        sigY = double.PositiveInfinity;
                    }
                }

                sigYk[k] = sigY;
                bool validGate = sigY > 0 && double.IsFinite(sigY) && double.IsFinite(y);
                // Exclude the two smallest gates where Poisson variance dominates and Z is unstable
                bool includeInZ = validGate && k >= 2 && N >= _minGateCountForZ;
                if (includeInZ)
                {
                    double z = Math.Abs(y / sigY);
                    if (z > maxAbsZ) maxAbsZ = z;
                }
                if (validGate)
                {
                    anyValidY = true;
                    allNonPositiveY &= y <= 0;
                }

                if (k == _tgIdx)
                {
                    // capture current gate values
                    selM1 = m1;
                    selM2 = m2;
                    selM3 = m3;
                    selN = N;
                    selY = y;
                    selSigY = sigY;
                    selV11 = cov.V11;
                    selV22 = cov.V22;
                    selV33 = cov.V33;
                    selV12 = cov.V12;
                    selV13 = cov.V13;
                    selV23 = cov.V23;
                }

                bool hasSig = sigY > 0 && double.IsFinite(sigY) && y > 0 && (y / sigY) >= _zMin;
                if (hasSig && significantIdx < 0) significantIdx = k;

                if (hasSig && k > significantIdx)
                {
                    double dlog = Math.Log((double)GateWidthUs(k) / GateWidthUs(k - 1));
                    if (dlog > 0)
                    {
                        double slope = Math.Abs((Yk[k] - Yk[k - 1]) / dlog);
                        double thresh = _etaFrac * Math.Abs(Yk[k]);
                        if (slope < thresh)
                        {
                            plateauIdx = k;
                        }
                    }
                }
            }

            if (selN < 2 || selM1 < 1e-6) _insufficientStatistics = true;

            // correlation-time fit across ladder
            _tauHat = FitCorrelationTime(Yk, sigYk, out _corrResiduals);
            double rms = (_corrResiduals != null && _corrResiduals.Length > 0) ? Rms(_corrResiduals) : double.PositiveInfinity;
            bool fitMeaningful = double.IsFinite(_tauHat) && double.IsFinite(rms);
            _modelMismatch = fitMeaningful && rms > (_epsY * 2.0);

            if (fitMeaningful && _modelMismatch)
            {
                _mismatchStreak++;
                _mismatchClearStreak = 0;
            }
            else if (fitMeaningful && !_modelMismatch)
            {
                _mismatchClearStreak++;
                _mismatchStreak = 0;
            }
            else
            {
                _mismatchClearStreak = 0;
                _mismatchStreak = 0;
            }

            int desiredIdx = _tgIdx;
            if (significantIdx < 0)
            {
                desiredIdx = 0;
            
            }
            else
            {
                if (plateauIdx >= significantIdx && plateauIdx >= 0)
                    desiredIdx = plateauIdx;
                else
                    desiredIdx = significantIdx;
            }

            UpdateGateSelection(desiredIdx);

            bool hasAnySignificance = significantIdx >= 0;
            bool hasSignificance = selSigY > 0 && double.IsFinite(selSigY) && selY > 0 && (selY / selSigY) >= _zMin;
            bool singlesChange = RateChange(selM1);
            bool correlationChange = RateChangeZy(selSigY > 0 && double.IsFinite(selSigY) ? selY / selSigY : 0);
            bool degraded = illConditioned || (allNonPositiveY && anyValidY);

            _insufficientStatistics = _insufficientStatistics || significantIdx < 0 || selSigY <= 0 || double.IsInfinity(selSigY);

            AdaptState(nowUs, selY, selSigY, selM1, selV11, selN, hasAnySignificance, singlesChange, correlationChange, degraded, maxAbsZ);

            // package results into Estimate
            var est = new Estimate
            {
                NowUs = nowUs,
                GateUs = GateWidthUs(_tgIdx),
                WindowSec = _W,
                M1 = selM1,
                M2 = selM2,
                M3 = selM3,
                VarM1 = selV11,
                VarM2 = selV22,
                VarM3 = selV33,
                CovM1M2 = selV12,
                CovM1M3 = selV13,
                CovM2M3 = selV23,
                Y = selY,
                SigmaY = selSigY,
                ZY = selSigY > 0 && double.IsFinite(selSigY) ? selY / selSigY : 0,
                State = _fsm.ToString(),
                HasSignificance = hasSignificance,
                IsLowRate = _fsm == FSM.LowRate,
                IsDegraded = _fsm == FSM.Degraded,
                IsStatsBound = _statsBound,
                InsufficientStatistics = _insufficientStatistics,
                ModelMismatch = _modelMismatch,
                MaxAbsZ = maxAbsZ,
                PoissonQuietStreak = _poissonQuietStreak
            };

            var counts = _acc.GetDetectorCounts(_W);
            double durationSeconds = Math.Max(0, _W);
            double totalCounts = counts.Sum();
            double rateTotalCps = durationSeconds > 0 ? totalCounts / durationSeconds : 0.0;
            var windowEndUtc = DateTimeOffset.UtcNow;

            var summary = new RtWindowSummary
            {
                WindowStartUtc = windowEndUtc.AddSeconds(-durationSeconds),
                WindowEndUtc = windowEndUtc,
                DurationSeconds = durationSeconds,
                Counts15 = counts,
                RtState = est.State,
                RateTotalCps = rateTotalCps,
                QualityScalar = est.ZY,
                IsConfusedCandidate = false
            };

            // log estimate
            var log = PreflightOracle.Run(
                DateTime.UtcNow,
                _fsm.ToString(),
                GateWidthUs(_tgIdx),
                _W,
                selN,
                selM1,
                selM2,
                selM3,
                selY,
                selSigY);

            var options = new System.Text.Json.JsonSerializerOptions
            {
                NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals,
                WriteIndented = false
            };

            string json = System.Text.Json.JsonSerializer.Serialize(log, options);

            if (_enableFileLog && !string.IsNullOrWhiteSpace(_logPath))
            {
                AppendLineShared(_logPath, json);
            }

            OnWindowSummary?.Invoke(summary);
            OnEstimate?.Invoke(est);
        }

        // thresholds for adaptation
        public double EpsY
        {
            get => _epsY;
            set => _epsY = Math.Max(1e-6, value);
        }
        public double EpsM1
        {
            get => _epsM1;
            set => _epsM1 = Math.Max(1e-6, value);
        }

        private void UpdateGateSelection(int desiredIdx)
        {
            if (desiredIdx == _tgIdx)
            {
                _pendingTgIdx = -1;
                _tgConfirmations = 0;
                return;
            }

            if (_pendingTgIdx != desiredIdx)
            {
                _pendingTgIdx = desiredIdx;
                _tgConfirmations = 1;
                return;
            }

            _tgConfirmations++;
            // If engine is in Hold, freeze Tg index
            if (_fsm == FSM.Hold)
            {
                _pendingTgIdx = -1;
                _tgConfirmations = 0;
                return;
            }
            if (_tgConfirmations >= 2)
            {
                // Only adopt plateau index if sigYk is finite
                // (use the selected gate's selSigY logic)
                if (_pendingTgIdx > _tgIdx && _insufficientStatistics)
                {
                    _pendingTgIdx = -1;
                    _tgConfirmations = 0;
                    return;
                }
                _tgIdx = Math.Clamp(desiredIdx, 0, _tgUs.Length - 1);
                _pendingTgIdx = -1;
                _tgConfirmations = 0;
            }
        }

        private double TauLowerBound() => (double.IsFinite(_tauHat) && _tauHat > 0) ? Math.Max(_wMin, 3.0 * _tauHat) : _wMin;

        private void AdaptWindow(double Y, double sigY, double m1, double varM1, bool honorTauFloor, double rDown = 0.5, double rUp = 2.0)
        {
            double relY = (Y > 0 && sigY > 0) ? sigY / Math.Max(Y, 1e-12) : double.PositiveInfinity;
            double relM1 = (m1 > 0 && varM1 >= 0) ? Math.Sqrt(varM1) / Math.Max(m1, 1e-12) : double.PositiveInfinity;

            if (relY < 0.5 * _epsY && relM1 < 0.5 * _epsM1)
            {
                _W = Math.Max(_W * 0.9, TauLowerBound());
            }
            else if (relY > 2 * _epsY || relM1 > 2 * _epsM1)
            {
                _W = Math.Min(_W * 1.2, _wMax);
            }

            // dissertation-required threshold triggers
            if (relY > _epsY || relM1 > _epsM1)
            {
                _W = Math.Min(_W * 1.3, _wMax);
            }
            else if (relY < _epsY && relM1 < _epsM1)
            {
                _W = Math.Max(_W * 0.95, TauLowerBound());
            }

            double scale = Math.Max(Sq(relY / _epsY), Sq(relM1 / _epsM1));
            if (double.IsFinite(scale) && scale > 0)
            {
                double req = Math.Clamp(_W * scale, _W * rDown, _W * rUp);
                double lower = honorTauFloor ? TauLowerBound() : _wMin;
                _W = Math.Clamp(req, lower, _wMax);
                _statsBound = _W >= _wMax && scale > 1.0;
            }

            _beta = BetaForState(_fsm);
        }

        private void AdaptState(long nowUs, double Y, double sigY, double m1, double varM1, int gatesUsed, bool hasSignificance, bool singlesChange, bool correlationChange, bool degraded, double maxAbsZ)
        {
            double relY = (Y > 0 && sigY > 0) ? sigY / Math.Max(Y, 1e-12) : double.PositiveInfinity;
            double relM1 = (m1 > 0 && varM1 >= 0) ? Math.Sqrt(varM1) / Math.Max(m1, 1e-12) : double.PositiveInfinity;
            double relMax = Math.Max(relY, relM1);
            double zy = sigY > 0 && double.IsFinite(sigY) ? Y / sigY : 0;
            bool needHold = singlesChange || correlationChange;

            if (_mismatchStreak >= _mismatchStreakRequired && _fsm != FSM.Degraded)
            {
                RequestFsmState(FSM.Degraded, nowUs);
            }

            switch (_fsm)
            {
                case FSM.Warmup:
                    if (_phAlarm)
                    {
                        RequestFsmState(FSM.Hold, nowUs);
                        break;
                    }
                    if (_insufficientStatistics)
                    {
                        RequestFsmState(FSM.LowRate, nowUs);
                        break;
                    }
                    _beta = BetaForState(_fsm);
                    bool windowFilled = (nowUs - _acc.LeftEdgeUs) >= (long)(_W * 1e6);
                    bool enoughGates = gatesUsed >= 2;
                    bool positiveM1 = m1 > 0;
                    if (maxAbsZ >= _zHold)
                    {
                        _poissonQuietStreak = 0;
                        RequestFsmState(FSM.Hold, nowUs);
                        break;
                    }

                    if (maxAbsZ >= _zTrack)
                    {
                        _poissonQuietStreak = 0;
                        RequestFsmState(FSM.Track, nowUs);
                        break;
                    }

                    if (windowFilled && enoughGates && positiveM1 && maxAbsZ < _zPoisson)
                    {
                        if (Math.Abs(zy) < _zPoisson) _poissonQuietStreak++;
                        else _poissonQuietStreak = 0;
                        if (_poissonQuietStreak >= _poissonQuietRequired) RequestFsmState(FSM.Poisson, nowUs);
                    }
                    else if (windowFilled)
                    {
                        _poissonQuietStreak = 0;
                    }
                    else
                    {
                        _poissonQuietStreak = 0;
                    }
                    break;

                case FSM.Poisson:
                    _beta = BetaForState(_fsm);
                    if (_phAlarm)
                    {
                        RequestFsmState(FSM.Hold, nowUs);
                        break;
                    }
                    if (_insufficientStatistics)
                    {
                        RequestFsmState(FSM.LowRate, nowUs);
                        break;
                    }
                    if (degraded)
                    {
                        RequestFsmState(FSM.Degraded, nowUs);
                        break;
                    }

                    if (maxAbsZ >= _zHold)
                    {
                        RequestFsmState(FSM.Hold, nowUs);
                    }
                    else if (maxAbsZ >= _zTrack)
                    {
                        RequestFsmState(FSM.Track, nowUs);
                    }
                    else
                    {
                        _W = Math.Max(_W, TauLowerBound());
                    }
                    break;

                case FSM.Track:
                    _beta = BetaForState(_fsm);
                    if (_phAlarm)
                    {
                        RequestFsmState(FSM.Hold, nowUs);
                        break;
                    }
                    if (degraded)
                    {
                        RequestFsmState(FSM.Degraded, nowUs);
                        break;
                    }
                    if (_insufficientStatistics)
                    {
                        RequestFsmState(FSM.LowRate, nowUs);
                        break;
                    }
                    if (needHold)
                    {
                        RequestFsmState(FSM.Hold, nowUs);
                        break;
                    }
                    if (relMax > 1.0 && _W < _wMax)
                    {
                        RequestFsmState(FSM.Expand, nowUs);
                    }
                    else if (relMax < 0.5 && _W > TauLowerBound())
                    {
                        RequestFsmState(FSM.Contract, nowUs);
                    }
                    else
                    {
                        AdaptWindow(Y, sigY, m1, varM1, false);
                    }

                    if (maxAbsZ < _zPoisson)
                    {
                        if (Math.Abs(zy) < _zPoisson) _poissonQuietStreak++;
                        else _poissonQuietStreak = 0;
                        if (_poissonQuietStreak >= _poissonQuietRequired) RequestFsmState(FSM.Poisson, nowUs);
                    }
                    else
                    {
                        _poissonQuietStreak = 0;
                    }
                    break;

                case FSM.Expand:
                    _beta = BetaForState(_fsm);
                    AdaptWindow(Y, sigY, m1, varM1, false, 0.8, 2.0);
                    if (relMax <= 1.0 || _W >= _wMax) RequestFsmState(FSM.Track, nowUs);
                    break;

                case FSM.Contract:
                    _beta = BetaForState(_fsm);
                    AdaptWindow(Y, sigY, m1, varM1, true, 0.5, 1.2);
                    if (relMax >= 0.8 || _W <= TauLowerBound()) RequestFsmState(FSM.Track, nowUs);
                    break;

                case FSM.Hold:
                    _beta = 0.1;
                    // freeze window: do not call AdaptWindow in Hold
                    _W = Math.Max(_W, TauLowerBound());
                    if (!needHold && nowUs >= _holdQuietUntilUs) RequestFsmState(FSM.Track, nowUs);
                    break;

                case FSM.LowRate:
                    _beta = 0.1;
                    _tgIdx = 0;
                    _W = Math.Min(_W * 1.2, _wMax);
                    if (hasSignificance) RequestFsmState(FSM.Track, nowUs);
                    break;

                case FSM.Degraded:
                    if (_phAlarm)
                    {
                        // Keep Degraded stable but clear the alarm so it doesn't cascade.
                        _phAlarm = false;
                    }
                    _beta = 0.1;
                    _tgIdx = 0;
                    _W = Math.Min(_W * 1.2, _wMax);
                    if (_mismatchClearStreak >= _mismatchClearRequired)
                    {
                        RequestFsmState(FSM.Track, nowUs);
                    }
                    break;
            }
        }

        private void RequestFsmState(FSM target, long nowUs)
        {
            if (target == _fsm)
            {
                _pendingFsm = _fsm;
                _fsmConfirmations = 0;
                return;
            }

            if (_pendingFsm != target)
            {
                _pendingFsm = target;
                _fsmConfirmations = 1;
                return;
            }

            _fsmConfirmations++;
            if (_fsmConfirmations >= 2)
            {
                EnterState(target, nowUs);
            }
        }

        private void EnterState(FSM target, long nowUs)
        {
            var prev = _fsm;
            _fsm = target;
            _pendingFsm = target;
            _fsmConfirmations = 0;
            if (target != FSM.Warmup)
            {
                _poissonQuietStreak = 0;
            }
            switch (target)
            {
                case FSM.Hold:
                    _phAlarm = false;
                    _pendingTgIdx = -1;
                    // freeze gate width
                    // (do not allow UpdateGateSelection to change _tgIdx while in Hold)
                    double tauFloor = TauLowerBound();
                    _W = Math.Max(_W, tauFloor);
                    _beta = 0.1;
                    _S = _beta * _W;
                    double horizon = Math.Max(5 * _S, 4 * TauLowerBound());
                    _holdQuietUntilUs = nowUs + (long)(horizon * 1e6);
                    break;
                case FSM.LowRate:
                    _tgIdx = 0;
                    _W = Math.Max(_W, _wMin);
                    _beta = 0.1;
                    break;
                case FSM.Degraded:
                    _tgIdx = 0;
                    _W = Math.Max(_W, _wMin);
                    _beta = 0.1;
                    break;
                default:
                    _holdQuietUntilUs = 0;
                    _beta = BetaForState(target);
                    break;
            }

            if (prev == FSM.Warmup && target == FSM.Track)
            {
                _S = 0.1 * _W;
            }
        }

        public double Beta
        {
            get => _beta;
            set => _beta = Math.Clamp(value, 0.01, 1.0);
        }

        private static double Sq(double x) => x * x;
        private static double Rms(double[] residuals)
        {
            if (residuals.Length == 0) return 0;
            double sum = 0;
            foreach (var r in residuals) sum += r * r;
            return Math.Sqrt(sum / residuals.Length);
        }

        // Page–Hinkley style change detection
        private bool RateChange(double x)
        {
            _cpMean = 0.99 * _cpMean + 0.01 * x;
            _cpCum += x - _cpMean - _cpDelta;
            if (_cpCum < 0) _cpCum = 0;
            bool alarm = _cpCum > _cpLambda;
            if (alarm) _phAlarm = true;
            return alarm;
        }

        private bool RateChangeZy(double zy)
        {
            _cpZyMean = 0.99 * _cpZyMean + 0.01 * zy;
            _cpZyCum += zy - _cpZyMean - _cpDelta;
            if (_cpZyCum < 0) _cpZyCum = 0;
            bool alarm = _cpZyCum > _cpLambda;
            if (alarm) _phAlarm = true;
            return alarm;
        }

        private double BetaForState(FSM s) => s == FSM.Track ? 0.5 : 0.1;

        private bool RegularizeCov(in MomentCovariance cov, out MomentCovariance reg)
        {
            double maxDiag = Math.Max(cov.V11, Math.Max(cov.V22, cov.V33));
            if (!double.IsFinite(maxDiag) || maxDiag <= 0)
            {
                reg = new MomentCovariance();
                return false;
            }

            double lambda = 1e-3 * Math.Max(1.0, maxDiag);
            double v11 = cov.V11 + lambda;
            double v22 = cov.V22 + lambda;
            double v33 = cov.V33 + lambda;
            reg = new MomentCovariance(v11, v22, v33, cov.V12, cov.V13, cov.V23);
            bool ok = v11 > 0 && v22 > 0 && v33 > 0 && double.IsFinite(cov.V12) && double.IsFinite(cov.V13) && double.IsFinite(cov.V23);
            return ok;
        }

        private static double Median(System.Collections.Generic.IEnumerable<double> values)
        {
            var arr = values.Where(double.IsFinite).OrderBy(v => v).ToArray();
            if (arr.Length == 0) return double.NaN;
            int mid = arr.Length / 2;
            return (arr.Length % 2 == 1) ? arr[mid] : 0.5 * (arr[mid - 1] + arr[mid]);
        }

        private double FitCorrelationTime(double[] Y, double[] sigY, out double[] residuals)
        {
            int n = Math.Min(Y.Length, sigY.Length);
            var samples = new System.Collections.Generic.List<(double t, double y, double sig, bool hasSig)>();

            for (int i = 0; i < n; i++)
            {
                if (i >= _tgUs.Length) break; // guard against ladder mismatch
                double t = GateWidthUs(i) / 1e6;
                double y = Y[i];
                double s = sigY[i];
                if (t <= 0 || !double.IsFinite(t) || !double.IsFinite(y) || y <= 0) continue;
                bool hasSig = s > 0 && double.IsFinite(s);
                samples.Add((t, y, hasSig ? s : 1.0, hasSig));
            }

            if (samples.Count < 2)
            {
                residuals = new[] { double.PositiveInfinity };
                return double.NaN;
            }

            // Reject obvious outliers using a MAD filter
            double median = Median(samples.Select(s => s.y));
            double mad = Median(samples.Select(s => Math.Abs(s.y - median)));
            double madThresh = mad > 0 ? 6.0 * mad : 0.25 * Math.Max(1e-6, median);
            samples = samples.Where(s => Math.Abs(s.y - median) <= madThresh).ToList();
            if (samples.Count < 2)
            {
                residuals = new[] { double.PositiveInfinity };
                return double.NaN;
            }

            double yMin = double.MaxValue, yMax = double.MinValue, yMean = 0;
            foreach (var s in samples)
            {
                yMin = Math.Min(yMin, s.y);
                yMax = Math.Max(yMax, s.y);
                yMean += s.y;
            }
            yMean /= samples.Count;

            double spread = yMax - yMin;
            double relSpread = spread / (Math.Abs(yMean) + 1e-12);
            if (relSpread < 0.02)
            {
                residuals = new[] { 0.5 }; // force mismatch for flat sequences
                return double.NaN;
            }

            double minT = samples.Min(s => s.t);
            double maxT = samples.Max(s => s.t);
            if (!(minT > 0) || !double.IsFinite(maxT) || maxT <= 0)
            {
                residuals = new[] { double.PositiveInfinity };
                return double.NaN;
            }

            int withSig = samples.Count(s => s.hasSig);
            double maxSig = samples.Where(s => s.hasSig).Select(s => s.sig).DefaultIfEmpty(1.0).Max();
            double minSig = samples.Where(s => s.hasSig).Select(s => s.sig).DefaultIfEmpty(1.0).Min();
            bool useWeights = withSig >= Math.Max(2, samples.Count / 2) && maxSig / Math.Max(minSig, 1e-12) < 1e6;

            double bestTau = double.NaN;
            double bestErr = double.PositiveInfinity;
            double bestYinf = double.NaN;

            double tMin = minT * 0.05;
            double tMax = maxT * 20.0;
            if (tMin <= 0 || tMax <= tMin * 1.01)
            {
                residuals = new[] { double.PositiveInfinity };
                return double.NaN;
            }

            int steps = 80;
            double logSpan = Math.Log(tMax / tMin);
            for (int sIdx = 0; sIdx < steps; sIdx++)
            {
                double tau = Math.Exp(Math.Log(tMin) + logSpan * sIdx / (steps - 1));
                double num = 0, den = 0, err = 0, wTot = 0;

                foreach (var smp in samples)
                {
                    double f = 1.0 - Math.Exp(-smp.t / tau);
                    if (!double.IsFinite(f) || f <= 0) continue;
                    double w = useWeights ? 1.0 / (smp.sig * smp.sig) : 1.0;
                    num += w * smp.y * f;
                    den += w * f * f;
                    wTot += w;
                }

                if (!(den > 0) || !(wTot > 0)) continue;
                double yInf = num / den;
                foreach (var smp in samples)
                {
                    double f = 1.0 - Math.Exp(-smp.t / tau);
                    if (!double.IsFinite(f) || f <= 0) continue;
                    double w = useWeights ? 1.0 / (smp.sig * smp.sig) : 1.0;
                    double diff = smp.y - yInf * f;
                    err += w * diff * diff;
                }

                double normErr = err / wTot;
                if (normErr < bestErr)
                {
                    bestErr = normErr;
                    bestTau = tau;
                    bestYinf = yInf;
                }
            }

            var resArr = new System.Collections.Generic.List<double>(samples.Count);
            if (double.IsFinite(bestTau) && double.IsFinite(bestYinf))
            {
                foreach (var smp in samples)
                {
                    double f = 1.0 - Math.Exp(-smp.t / bestTau);
                    if (!double.IsFinite(f) || f <= 0) continue;
                    double model = bestYinf * f;
                    resArr.Add(smp.y - model);
                }
            }
            else
            {
                resArr.Add(double.PositiveInfinity);
            }

            residuals = resArr.ToArray();
            return bestTau;
        }

        // Accumulator for counts in fixed bins
        private sealed class BaseBinAccumulator
        {
            public readonly int DeltaUs; // bin width (µs)
            public readonly int MaxBins; // number of bins in circular buffer
            private readonly int[] _counts; // count per bin
            private long _t0Us;  // left edge of buffer
            private int _head;   // head pointer in ring buffer
            private int _total;  // total counts currently stored

            private readonly int _detectorCount;
            private readonly int[,] _countsByDetector;

            public BaseBinAccumulator(int deltaUs, double wmaxSec, int detectorCount)
            {
                DeltaUs = deltaUs;
                MaxBins = (int)Math.Ceiling(wmaxSec * 1e6 / deltaUs);
                _counts = new int[MaxBins];
                _detectorCount = detectorCount;
                _countsByDetector = new int[_detectorCount, MaxBins];
            }

            public long RightEdgeUs => _t0Us + (long)MaxBins * DeltaUs;

            // Slide buffer left to new time origin
            public void SlideLeftTo(long newT0Us)
            {
                if (_t0Us == 0) { _t0Us = newT0Us; return; }
                if (newT0Us <= _t0Us) return;
                int bins = (int)((newT0Us - _t0Us) / DeltaUs);
                for (int i = 0; i < bins && i < MaxBins; i++)
                {
                    _total -= _counts[_head];
                    _counts[_head] = 0;
                    for (int det = 0; det < _detectorCount; det++)
                    {
                        _countsByDetector[det, _head] = 0;
                    }
                    _head = (_head + 1) % MaxBins;
                }
                _t0Us += (long)Math.Min(bins, MaxBins) * DeltaUs;
            }

            // Add detection event
            public void Add(long tUs, byte detectorId)
            {
                if (_t0Us == 0) _t0Us = (tUs / DeltaUs) * DeltaUs;
                long rightUs = _t0Us + (long)MaxBins * DeltaUs;
                if (tUs >= rightUs)
                {
                    long needLeft = tUs - ((long)(MaxBins - 1) * DeltaUs);
                    SlideLeftTo(needLeft);
                }
                int idx = (int)((tUs - _t0Us) / DeltaUs);
                if (idx < 0 || idx >= MaxBins) return;
                int phys = (_head + idx) % MaxBins;
                _counts[phys]++;
                if (detectorId < _detectorCount)
                {
                    _countsByDetector[detectorId, phys]++;
                }
                _total++;
            }

            public void ResetTo(long tUs)
            {
                Array.Clear(_counts, 0, _counts.Length);
                Array.Clear(_countsByDetector, 0, _countsByDetector.Length);
                _head = 0;
                _total = 0;
                _t0Us = (tUs / DeltaUs) * DeltaUs;
            }

            // Compute factorial moments over gates within current window
            public void ComputeMoments(double wSec, int gateUs, out double m1, out double m2, out double m3, out int N, out MomentCovariance cov)
            {
                int binsPerGate = Math.Max(1, gateUs / DeltaUs);
                int gatesInWindow = Math.Max(1, (int)Math.Floor(wSec * 1e6 / gateUs));
                N = Math.Min(gatesInWindow, MaxBins / binsPerGate);
                if (N <= 0) { m1 = m2 = m3 = 0; cov = new MomentCovariance(); return; }

                int startPhys = _head;

                // initial sum for first gate
                int sum = 0;
                for (int b = 0; b < binsPerGate; b++) sum += _counts[(startPhys + b) % MaxBins];

                long s1 = 0, s2 = 0, s3 = 0;
                double sumN2 = 0, sumF22 = 0, sumF32 = 0;
                double sumNF2 = 0, sumNF3 = 0, sumF2F3 = 0;
                int binPtr = (startPhys + binsPerGate) % MaxBins;

                // slide gate across window
                for (int g = 0; g < N; g++)
                {
                    int nj = sum;
                    s1 += nj;
                    s2 += (long)nj * (nj - 1);
                    s3 += (long)nj * (nj - 1) * (nj - 2);

                    double f2 = (double)nj * (nj - 1);
                    double f3 = (double)nj * (nj - 1) * (nj - 2);

                    sumN2 += (double)nj * nj;
                    sumF22 += f2 * f2;
                    sumF32 += f3 * f3;
                    sumNF2 += nj * f2;
                    sumNF3 += nj * f3;
                    sumF2F3 += f2 * f3;

                    if (g < N - 1)
                    {
                        for (int b = 0; b < binsPerGate; b++)
                        {
                            int outIdx = (startPhys + g * binsPerGate + b) % MaxBins;
                            sum -= _counts[outIdx];
                            sum += _counts[(binPtr + b) % MaxBins];
                        }
                        binPtr = (binPtr + binsPerGate) % MaxBins;
                    }
                }

                double invN = 1.0 / N;
                m1 = s1 * invN;
                m2 = s2 * invN;
                m3 = s3 * invN;

                // sample covariance of gate-level factorial moments
                double varN = Math.Max(0.0, (sumN2 - N * m1 * m1) / Math.Max(1, N - 1));
                double varF2 = Math.Max(0.0, (sumF22 - N * m2 * m2) / Math.Max(1, N - 1));
                double varF3 = Math.Max(0.0, (sumF32 - N * m3 * m3) / Math.Max(1, N - 1));
                double covNF2 = (sumNF2 - N * m1 * m2) / Math.Max(1, N - 1);
                double covNF3 = (sumNF3 - N * m1 * m3) / Math.Max(1, N - 1);
                double covF2F3 = (sumF2F3 - N * m2 * m3) / Math.Max(1, N - 1);

                cov = new MomentCovariance(varN * invN, varF2 * invN, varF3 * invN,
                    covNF2 * invN, covNF3 * invN, covF2F3 * invN);
            }

            public long LeftEdgeUs => _t0Us;
            public int SinglesInWindow => _total;
            public int TotalEvents => _total;

            public int CountNonEmptyBins()
            {
                int bins = 0;
                for (int i = 0; i < MaxBins; i++)
                {
                    if (_counts[i] > 0) bins++;
                }

                return bins;
            }

            public double[] GetDetectorCounts(double wSec)
            {
                var counts = new double[_detectorCount];
                if (_detectorCount == 0 || wSec <= 0)
                {
                    return counts;
                }

                int binsInWindow = Math.Max(1, (int)Math.Floor(wSec * 1e6 / DeltaUs));
                binsInWindow = Math.Min(binsInWindow, MaxBins);

                for (int b = 0; b < binsInWindow; b++)
                {
                    int idx = (_head + b) % MaxBins;
                    for (int det = 0; det < _detectorCount; det++)
                    {
                        counts[det] += _countsByDetector[det, idx];
                    }
                }

                return counts;
            }
        }

        // Math utilities for Y statistic and error propagation
        private static class MomentsMath
        {
            // Compute Feynman-Y from m1 and m2
            public static double Y(double m1, double m2)
            {
                if (m1 <= 0) return 0;
                return (m2 - m1 * m1) / m1;
            }

            // Compute gradient of Y wrt m1, m2
            public static void GradY(double m1, double m2, out double dY_dm1, out double dY_dm2)
            {
                if (m1 <= 0) { dY_dm1 = dY_dm2 = 0; return; }
                dY_dm2 = 1.0 / m1;
                dY_dm1 = -m2 / (m1 * m1) - 1.0;
            }

            // Propagate variance of Y from variances/covariance of m1 and m2
            public static double VarY(double m1, double m2, double v11, double v22, double v12)
            {
                GradY(m1, m2, out var a, out var b);
                return Math.Max(0.0, a * a * v11 + b * b * v22 + 2 * a * b * v12);
            }

        }
    }
}

