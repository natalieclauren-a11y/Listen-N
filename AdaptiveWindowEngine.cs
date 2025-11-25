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
    using System.Threading;
    using System.Threading.Channels;

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
        }

        public event Action<Estimate>? OnEstimate; // callback for new estimates

        // ——— configuration ———
        private readonly int[] _tgUs;   // available gate widths (µs)
        private readonly int _deltaUs;  // base bin width (µs)
        private readonly double _wMin;  // min window size (s)
        private readonly double _wMax;  // max window size (s)
        private int _zMin = 3;          // minimum significance threshold

        public int Zmin
        {
            get => _zMin;
            set => _zMin = Math.Max(1, value);
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

        // ——— runtime state ———
        private readonly BaseBinAccumulator _acc; // accumulator of counts
        private readonly Channel<Detection> _inbound; // inbound channel
        private readonly Thread? _worker;             // worker thread
        private readonly bool _startWorker;
        private volatile bool _running = true;       // loop control flag

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
        private enum FSM { Warmup, Track, Expand, Contract, Hold, LowRate, Degraded }
        private FSM _fsm = FSM.Warmup;

        private int _pendingTgIdx = -1;
        private int _tgConfirmations = 0;

        private int _tgIdx;      // current gate index in ladder
        private double _W;       // current window size (s)
        private double _beta;    // step fraction for sliding window
        private double _S;       // step size (s)

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
        private double _cpZyMean, _cpZyCum;

        public AdaptiveWindowEngine(
            int baseDeltaUs = 500,
            double windowStartSec = 2.0,
            double windowMinSec = 0.5,
            double windowMaxSec = 60.0,
            int[]? gateLadderUs = null,
            int zMin = 3,
            double epsY = 0.10,
            double epsM1 = 0.02,
            bool startWorker = true)
        {
            _deltaUs = baseDeltaUs;
            _tgUs = gateLadderUs ?? new[] { 500, 1000, 2000, 4000, 8000, 16000, 32000 };
            _tgIdx = Math.Min(1, _tgUs.Length - 1);
            _wMin = windowMinSec;
            _wMax = windowMaxSec;
            _W = windowStartSec;
            _beta = 0.1;
            _zMin = zMin;
            _epsY = epsY;
            _epsM1 = epsM1;
            _startWorker = startWorker;

            // initialize accumulator and channel infrastructure
            _acc = new BaseBinAccumulator(_deltaUs, _wMax);
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
                InitializeNextStep();

                long nowUs = _acc.LeftEdgeUs + (long)(_W * 1e6);
                while (_nextStepUs != 0 && nowUs >= _nextStepUs)
                {
                    Step(_nextStepUs);
                    _nextStepUs = _nextStepUs + (long)(StepSizeSec() * 1e6);
                }
                Thread.SpinWait(256);
            }
        }

        private void InitializeNextStep()
        {
            if (_nextStepUs != 0) return;
            if (_acc.LeftEdgeUs == 0) return;
            _nextStepUs = _acc.LeftEdgeUs + (long)(_W * 1e6);
        }

        private void DrainInbound(ChannelReader<Detection>? reader = null)
        {
            var r = reader ?? _inbound.Reader;
            while (r.TryRead(out var d)) _acc.Add(d.TicksUs);
        }

        public void ForceEstimate(long nowUs)
        {
            DrainInbound();
            InitializeNextStep();
            if (_nextStepUs == 0) return;

            while (nowUs >= _nextStepUs)
            {
                Step(_nextStepUs);
                _nextStepUs = _nextStepUs + (long)(StepSizeSec() * 1e6);
            }
        }

        // Compute adaptive step size for sliding window
        private double StepSizeSec()
        {
            _S = Math.Max(_deltaUs / 1e6, _beta * _W);
            _S = Math.Clamp(_S, 1e-3, _wMax);
            return _S;
        }

        // Perform one analysis step
        private void Step(long nowUs)
        {
            // Slide accumulator to discard old bins
            _acc.SlideLeftTo(nowUs - (long)(_wMax * 1e6));

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

            // covariance entries for selected gate
            double selV11 = 0, selV22 = 0, selV12 = 0;
            bool illConditioned = false;
            bool anyValidY = false;
            bool allNonPositiveY = true;

            // loop across ladder of gate widths
            for (int k = 0; k < _tgUs.Length; k++)
            {
                _acc.ComputeMoments(_W, _tgUs[k],
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
                    sigY = double.IsFinite(varY) && varY > 0 ? Math.Sqrt(varY) : double.PositiveInfinity;
                }

                sigYk[k] = sigY;
                bool validGate = sigY > 0 && double.IsFinite(sigY) && double.IsFinite(y);
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
                    selV12 = cov.V12;
                }

                bool hasSig = sigY > 0 && double.IsFinite(sigY) && y > 0 && (y / sigY) >= _zMin;
                if (hasSig && significantIdx < 0) significantIdx = k;

                if (hasSig && k > 0 && significantIdx >= 0 && k >= significantIdx)
                {
                    double dlog = Math.Log((double)_tgUs[k] / _tgUs[k - 1]);
                    if (dlog > 0)
                    {
                        double slope = Math.Abs((Yk[k] - Yk[k - 1]) / dlog);
                        double eta = _etaFrac * Math.Abs(Yk[k]);
                        if (slope <= eta)
                        {
                            plateauIdx = k;
                        }
                    }
                }
            }

            // correlation-time fit across ladder
            _tauHat = FitCorrelationTime(Yk, sigYk, out _corrResiduals);
            _modelMismatch = _corrResiduals.Length > 0 && Rms(_corrResiduals) > (_epsY * 2.0);

            int desiredIdx = _tgIdx;
            if (significantIdx < 0)
            {
                desiredIdx = 0;
                RequestFsmState(FSM.LowRate, nowUs);
            }
            else
            {
                desiredIdx = plateauIdx >= significantIdx && plateauIdx >= 0 ? plateauIdx : significantIdx;
            }

            UpdateGateSelection(desiredIdx);

            bool hasAnySignificance = significantIdx >= 0;
            bool hasSignificance = selSigY > 0 && double.IsFinite(selSigY) && selY > 0 && (selY / selSigY) >= _zMin;
            bool singlesChange = RateChange(selM1);
            bool correlationChange = RateChangeZy(selSigY > 0 && double.IsFinite(selSigY) ? selY / selSigY : 0);
            bool degraded = illConditioned || (allNonPositiveY && anyValidY);

            _insufficientStatistics = significantIdx < 0 || selSigY <= 0 || double.IsInfinity(selSigY);

            AdaptState(nowUs, selY, selSigY, selM1, selV11, hasAnySignificance, singlesChange, correlationChange, degraded);

            // package results into Estimate
            var est = new Estimate
            {
                NowUs = nowUs,
                GateUs = _tgUs[_tgIdx],
                WindowSec = _W,
                M1 = selM1,
                M2 = selM2,
                M3 = selM3,
                Y = selY,
                SigmaY = selSigY,
                ZY = selSigY > 0 && double.IsFinite(selSigY) ? selY / selSigY : 0,
                State = _fsm.ToString(),
                HasSignificance = hasSignificance,
                IsLowRate = _fsm == FSM.LowRate,
                IsDegraded = _fsm == FSM.Degraded,
                IsStatsBound = _statsBound,
                InsufficientStatistics = _insufficientStatistics,
                ModelMismatch = _modelMismatch
            };

            // log estimate
            var log = PreflightOracle.Run(
                DateTime.UtcNow,
                _fsm.ToString(),
                _tgUs[_tgIdx],
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
            System.IO.File.AppendAllText("adaptive.log", json + Environment.NewLine);

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
            if (_tgConfirmations >= 2)
            {
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

        private void AdaptState(long nowUs, double Y, double sigY, double m1, double varM1, bool hasSignificance, bool singlesChange, bool correlationChange, bool degraded)
        {
            double relY = (Y > 0 && sigY > 0) ? sigY / Math.Max(Y, 1e-12) : double.PositiveInfinity;
            double relM1 = (m1 > 0 && varM1 >= 0) ? Math.Sqrt(varM1) / Math.Max(m1, 1e-12) : double.PositiveInfinity;
            double relMax = Math.Max(relY, relM1);
            bool needHold = singlesChange || correlationChange;

            switch (_fsm)
            {
                case FSM.Warmup:
                    _beta = BetaForState(_fsm);
                    if ((nowUs - _acc.LeftEdgeUs) >= (long)(_W * 1e6)) RequestFsmState(FSM.Track, nowUs);
                    else if (hasSignificance && !degraded) RequestFsmState(FSM.Track, nowUs);
                    break;

                case FSM.Track:
                    _beta = BetaForState(_fsm);
                    if (degraded)
                    {
                        RequestFsmState(FSM.Degraded, nowUs);
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
                    _beta = 0.1;
                    _tgIdx = 0;
                    _W = Math.Min(_W * 1.1, _wMax);
                    if (!degraded && hasSignificance) RequestFsmState(FSM.Track, nowUs);
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
            _fsm = target;
            _pendingFsm = target;
            _fsmConfirmations = 0;
            switch (target)
            {
                case FSM.Hold:
                    _W = Math.Max(_W, TauLowerBound());
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
            if (_cpMean == 0) _cpMean = x;
            _cpMean = 0.99 * _cpMean + 0.01 * x;
            _cpCum += x - _cpMean - _cpDelta;
            if (_cpCum < 0) _cpCum = 0;
            return _cpCum > _cpLambda;
        }

        private bool RateChangeZy(double zy)
        {
            if (_cpZyMean == 0) _cpZyMean = zy;
            _cpZyMean = 0.99 * _cpZyMean + 0.01 * zy;
            _cpZyCum += zy - _cpZyMean - _cpDelta;
            if (_cpZyCum < 0) _cpZyCum = 0;
            return _cpZyCum > _cpLambda;
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

            double lambda = 1e-12 * Math.Max(1.0, maxDiag);
            double v11 = cov.V11 + lambda;
            double v22 = cov.V22 + lambda;
            double v33 = cov.V33 + lambda;
            reg = new MomentCovariance(v11, v22, v33, cov.V12, cov.V13, cov.V23);
            bool ok = v11 > 0 && v22 > 0 && v33 > 0 && double.IsFinite(cov.V12) && double.IsFinite(cov.V13) && double.IsFinite(cov.V23);
            return ok;
        }

        private double FitCorrelationTime(double[] Y, double[] sigY, out double[] residuals)
        {
            int n = Math.Min(Y.Length, sigY.Length);
            double minT = double.MaxValue, maxT = 0;
            double wSum = 0, ySum = 0;
            for (int i = 0; i < n; i++)
            {
                if (sigY[i] > 0 && double.IsFinite(sigY[i]) && double.IsFinite(Y[i]))
                {
                    double w = 1.0 / (sigY[i] * sigY[i]);
                    wSum += w;
                    ySum += w * Y[i];
                    double t = _tgUs[i] / 1e6;
                    minT = Math.Min(minT, t);
                    maxT = Math.Max(maxT, t);
                }
            }

            if (wSum <= 0 || !double.IsFinite(minT) || minT <= 0)
            {
                residuals = Array.Empty<double>();
                return double.NaN;
            }

            double yInf = ySum / wSum;
            double bestTau = double.NaN;
            double bestErr = double.PositiveInfinity;
            double tMin = minT * 0.25;
            double tMax = maxT * 10.0;

            for (int s = 0; s < 40; s++)
            {
                double logTau = Math.Log(tMin) + (Math.Log(tMax / tMin) * s) / 39.0;
                double tau = Math.Exp(logTau);
                double err = 0;
                for (int i = 0; i < n; i++)
                {
                    if (sigY[i] <= 0 || !double.IsFinite(sigY[i]) || !double.IsFinite(Y[i])) continue;
                    double w = 1.0 / (sigY[i] * sigY[i]);
                    double t = _tgUs[i] / 1e6;
                    double model = yInf * (1.0 - Math.Exp(-t / tau));
                    double diff = Y[i] - model;
                    err += w * diff * diff;
                }
                if (err < bestErr)
                {
                    bestErr = err;
                    bestTau = tau;
                }
            }

            var resArr = new System.Collections.Generic.List<double>(n);
            if (double.IsFinite(bestTau))
            {
                for (int i = 0; i < n; i++)
                {
                    if (sigY[i] <= 0 || !double.IsFinite(sigY[i]) || !double.IsFinite(Y[i])) continue;
                    double t = _tgUs[i] / 1e6;
                    double model = yInf * (1.0 - Math.Exp(-t / bestTau));
                    resArr.Add(Y[i] - model);
                }
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

            public BaseBinAccumulator(int deltaUs, double wmaxSec)
            {
                DeltaUs = deltaUs;
                MaxBins = (int)Math.Ceiling(wmaxSec * 1e6 / deltaUs);
                _counts = new int[MaxBins];
            }

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
                    _head = (_head + 1) % MaxBins;
                }
                _t0Us += (long)Math.Min(bins, MaxBins) * DeltaUs;
            }

            // Add detection event
            public void Add(long tUs)
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
                _total++;
            }

            // Compute factorial moments over gates within current window
            public void ComputeMoments(double wSec, int gateUs, out double m1, out double m2, out double m3, out int N, out MomentCovariance cov)
            {
                int binsPerGate = Math.Max(1, gateUs / DeltaUs);
                int gatesInWindow = Math.Max(1, (int)Math.Floor(wSec * 1e6 / gateUs));
                N = Math.Min(gatesInWindow, MaxBins / binsPerGate);
                if (N <= 0) { m1 = m2 = m3 = 0; cov = new MomentCovariance(); return; }

                int windowBins = N * binsPerGate;
                int startPhys = _head + (MaxBins - windowBins);
                while (startPhys < 0) startPhys += MaxBins;
                startPhys %= MaxBins;

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

                double invN2 = 1.0 / Math.Max(1, N);
                cov = new MomentCovariance(varN * invN2, varF2 * invN2, varF3 * invN2,
                    covNF2 * invN2, covNF3 * invN2, covF2F3 * invN2);
            }

            public long LeftEdgeUs => _t0Us;
            public int SinglesInWindow => _total;
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
