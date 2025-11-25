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
            public double Y { get; init; }         // Feynman-Y statistic
            public double SigmaY { get; init; }    // uncertainty of Y
            public double ZY { get; init; }        // Z-score of Y (Y / σY)
            public string State { get; init; } = "Track"; // finite state machine state
            public bool HasSignificance { get; init; }     // significance flag
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
        private readonly Thread _worker;             // worker thread
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

        // Page–Hinkley change-point detection variables
        private double _cpMean, _cpCum;
        private readonly double _cpDelta = 5e-3, _cpLambda = 50.0;

        public AdaptiveWindowEngine(
            int baseDeltaUs = 500,
            double windowStartSec = 2.0,
            double windowMinSec = 0.5,
            double windowMaxSec = 60.0,
            int[]? gateLadderUs = null,
            int zMin = 3,
            double epsY = 0.10,
            double epsM1 = 0.02)
        {
            _deltaUs = baseDeltaUs;
            _tgUs = gateLadderUs ?? new[] { 500, 1000, 2000, 4000, 8000, 16000, 32000 };
            _tgIdx = Math.Min(1, _tgUs.Length - 1);
            _wMin = windowMinSec;
            _wMax = windowMaxSec;
            _W = windowStartSec;
            _beta = 0.25;
            _zMin = zMin;
            _epsY = epsY;
            _epsM1 = epsM1;

            // initialize accumulator and channel infrastructure
            _acc = new BaseBinAccumulator(_deltaUs, _wMax);
            _inbound = Channel.CreateBounded<Detection>(new BoundedChannelOptions(1 << 16)
            {
                SingleWriter = false,
                SingleReader = true
            });
            _worker = new Thread(Worker) { IsBackground = true, Name = "AdaptiveWindowEngine" };
            _worker.Start();
        }

        public void Dispose()
        {
            _running = false;
            _worker.Join();
        }

        // Push a new detection into channel (non-blocking)
        public void OnDetection(in Detection d)
        {
            _inbound.Writer.TryWrite(d);
        }

        // ——— worker loop ———
        private void Worker()
        {
            long nextStepUs = 0;
            var r = _inbound.Reader;
            while (_running)
            {
                // drain all pending detections
                while (r.TryRead(out var d)) _acc.Add(d.TicksUs);

                if (nextStepUs == 0) nextStepUs = _acc.LeftEdgeUs + (long)(_W * 1e6);
                long nowUs = _acc.LeftEdgeUs + (long)(_W * 1e6);

                if (nowUs >= nextStepUs)
                {
                    Step(nowUs);
                    nextStepUs = nowUs + (long)(StepSizeSec() * 1e6);
                }

                Thread.SpinWait(256);
            }
        }

        // Compute adaptive step size for sliding window
        private double StepSizeSec()
        {
            double s = Math.Max(_deltaUs / 1e6, _beta * _W);
            return Math.Clamp(s, 1e-3, _wMax);
        }

        // Perform one analysis step
        private void Step(long nowUs)
        {
            // Slide accumulator to discard old bins
            _acc.SlideLeftTo(nowUs - (long)(_wMax * 1e6));

            int bestIdx = _tgIdx;
            double selM1 = 0, selM2 = 0, selM3 = 0;
            int selN = 0;
            double selY = 0, selSigY = 0;
            double selRelY = double.PositiveInfinity, selRelM1 = double.PositiveInfinity;

            int significantIdx = -1;
            int plateauIdx = -1;

            double[] Yk = new double[_tgUs.Length];
            double[] sigYk = new double[_tgUs.Length];

            // covariance entries for selected gate
            double selV11 = 0, selV22 = 0, selV12 = 0;

            // loop across ladder of gate widths
            for (int k = 0; k < _tgUs.Length; k++)
            {
                _acc.ComputeMoments(_W, _tgUs[k],
                    out var m1, out var m2, out var m3, out var N,
                    out var cov);

                // compute Feynman-Y and a rough uncertainty estimate using delta method
                var y = MomentsMath.Y(m1, m2);
                Yk[k] = y;

                double sigY = double.PositiveInfinity;
                if (N > 1)
                {
                    double varY = MomentsMath.VarY(m1, m2, cov.V11, cov.V22, cov.V12);
                    sigY = double.IsFinite(varY) && varY > 0 ? Math.Sqrt(varY) : double.PositiveInfinity;
                }

                sigYk[k] = sigY;

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
                    selRelY = (sigY > 0 && double.IsFinite(sigY) && y > 0) ? sigY / y : double.PositiveInfinity;
                    selRelM1 = cov.V11 > 0 ? Math.Sqrt(cov.V11) / Math.Max(m1, 1e-12) : double.PositiveInfinity;
                }

                bool hasSig = sigY > 0 && double.IsFinite(sigY) && y > 0 && (y / sigY) >= _zMin;
                if (hasSig && significantIdx < 0) significantIdx = k;

                if (hasSig && k > 0 && significantIdx >= 0 && plateauIdx < 0)
                {
                    double dlog = Math.Log((double)_tgUs[k] / _tgUs[k - 1]);
                    if (dlog > 0)
                    {
                        double slope = Math.Abs((Yk[k] - Yk[k - 1]) / dlog);
                        double eta = _etaFrac * Math.Abs(Yk[k]);
                        if (slope <= eta) plateauIdx = k;
                    }
                }
            }

            int desiredIdx = _tgIdx;
            if (significantIdx < 0)
            {
                desiredIdx = 0;
                _fsm = FSM.LowRate;
            }
            else
            {
                desiredIdx = plateauIdx >= significantIdx && plateauIdx >= 0 ? plateauIdx : significantIdx;
            }

            UpdateGateSelection(desiredIdx);

            // package results into Estimate
            var est = new Estimate
            {
                NowUs = nowUs,
                GateUs = _tgUs[_tgIdx],
                WindowSec = _W,
                M1 = selM1,
                Y = selY,
                SigmaY = selSigY,
                ZY = selSigY > 0 && double.IsFinite(selSigY) ? selY / selSigY : 0,
                State = _fsm.ToString(),
                HasSignificance = selSigY > 0 && double.IsFinite(selSigY)
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

            AdaptState(selY, selSigY, selM1, selV11);
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

        // Adapt window size based on uncertainty
        private void AdaptWindow(double Y, double sigY, double m1, double varM1)
        {
            double relY = (Y > 0 && sigY > 0) ? sigY / Math.Max(Y, 1e-12) : double.PositiveInfinity;
            double relM1 = (m1 > 0 && varM1 >= 0) ? Math.Sqrt(varM1) / Math.Max(m1, 1e-12) : double.PositiveInfinity;

            double scale = Math.Max(Sq(relY / _epsY), Sq(relM1 / _epsM1));
            if (double.IsFinite(scale) && scale > 0)
            {
                double rDown = 0.5, rUp = 2.0;
                double req = Math.Clamp(_W * scale, _W * rDown, _W * rUp);
                _W = Math.Clamp(req, _wMin, _wMax);
            }

            _beta = (relY < 0.8 && relM1 < 0.8) ? 0.5 : 0.25;
        }

        private void AdaptState(double Y, double sigY, double m1, double varM1)
        {
            double relY = (Y > 0 && sigY > 0) ? sigY / Math.Max(Y, 1e-12) : double.PositiveInfinity;
            double relM1 = (m1 > 0 && varM1 >= 0) ? Math.Sqrt(varM1) / Math.Max(m1, 1e-12) : double.PositiveInfinity;
            double relMax = Math.Max(relY, relM1);

            bool changeDetected = RateChange(m1);
            bool degraded = double.IsNaN(sigY) || double.IsInfinity(sigY);

            switch (_fsm)
            {
                case FSM.Warmup:
                    if (!degraded && double.IsFinite(relMax)) _fsm = FSM.Track;
                    break;

                case FSM.Track:
                    if (_fsm == FSM.LowRate) break;
                    if (degraded)
                    {
                        _fsm = FSM.Degraded;
                        break;
                    }
                    if (changeDetected)
                    {
                        _fsm = FSM.Hold;
                        _beta = 0.1;
                        break;
                    }
                    if (relMax > 1.0 && _W < _wMax)
                    {
                        _fsm = FSM.Expand;
                    }
                    else if (relMax < 0.5 && _W > _wMin)
                    {
                        _fsm = FSM.Contract;
                    }
                    break;

                case FSM.Expand:
                    AdaptWindow(Y, sigY, m1, varM1);
                    if (relMax <= 1.0 || _W >= _wMax) _fsm = FSM.Track;
                    break;

                case FSM.Contract:
                    AdaptWindow(Y, sigY, m1, varM1);
                    if (relMax >= 0.8 || _W <= _wMin) _fsm = FSM.Track;
                    break;

                case FSM.Hold:
                    _beta = 0.1;
                    if (!changeDetected) _fsm = FSM.Track;
                    break;

                case FSM.LowRate:
                    _tgIdx = 0;
                    _W = Math.Min(_W * 1.2, _wMax);
                    if (Y > 0 && sigY > 0 && (Y / sigY) >= _zMin) _fsm = FSM.Track;
                    break;

                case FSM.Degraded:
                    _tgIdx = 0;
                    _W = Math.Min(_W * 1.5, _wMax);
                    if (!degraded && sigY > 0 && double.IsFinite(sigY)) _fsm = FSM.Track;
                    break;
            }

            if (_fsm == FSM.Track)
            {
                AdaptWindow(Y, sigY, m1, varM1);
            }
        }

        public double Beta
        {
            get => _beta;
            set => _beta = Math.Clamp(value, 0.01, 1.0);
        }

        private static double Sq(double x) => x * x;

        // Page–Hinkley style change detection
        private bool RateChange(double x)
        {
            if (_cpMean == 0) _cpMean = x;
            _cpMean = 0.99 * _cpMean + 0.01 * x;
            _cpCum += x - _cpMean - _cpDelta;
            if (_cpCum < 0) _cpCum = 0;
            return _cpCum > _cpLambda;
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
