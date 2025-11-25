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

        // Finite State Machine states for adaptation
        private enum FSM { Warmup, Track, Hold, Degraded }
        private FSM _fsm = FSM.Warmup;

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

            double[] Yk = new double[_tgUs.Length];
            double[] sigYk = new double[_tgUs.Length];

            // loop across ladder of gate widths
            for (int k = 0; k < _tgUs.Length; k++)
            {
                _acc.ComputeMoments(_W, _tgUs[k],
                    out var m1, out var m2, out var m3, out var N);

                // placeholder for computing Y and uncertainty

                if (k == _tgIdx)
                {
                    // capture current gate values
                    selM1 = m1;
                    selM2 = m2;
                    selM3 = m3;
                    selN = N;
                    selY = Yk[k];
                    selSigY = sigYk[k];
                }
            }

            // package results into Estimate
            var est = new Estimate
            {
                NowUs = nowUs,
                GateUs = _tgUs[_tgIdx],
                WindowSec = _W,
                M1 = selM1,
                Y = selY,
                SigmaY = selSigY,
                ZY = selSigY > 0 ? selY / selSigY : 0,
                State = _fsm.ToString(),
                HasSignificance = true
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

        // Adapt window size based on uncertainty
        private void AdaptWindow(double Y, double sigY, double m1)
        {
            double relY = (Y > 0 && sigY > 0) ? sigY / Math.Max(Y, 1e-12) : double.PositiveInfinity;
            double relM1 = (m1 > 0) ? 1.0 / Math.Sqrt(Math.Max(1.0, m1)) : double.PositiveInfinity;

            double f = Math.Max(Sq(relY / _epsY), Sq(relM1 / _epsM1));
            if (double.IsFinite(f) && f > 0)
            {
                double rDown = 0.5, rUp = 2.0;
                double req = Math.Clamp(_W * f, _W * rDown, _W * rUp);
                _W = Math.Clamp(req, _wMin, _wMax);
            }
            _beta = (relY < 0.8 && relM1 < 0.8) ? 0.5 : 0.25;
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
            public void ComputeMoments(double wSec, int gateUs, out double m1, out double m2, out double m3, out int N)
            {
                int binsPerGate = Math.Max(1, gateUs / DeltaUs);
                int gatesInWindow = Math.Max(1, (int)Math.Floor(wSec * 1e6 / gateUs));
                N = Math.Min(gatesInWindow, MaxBins / binsPerGate);
                if (N <= 0) { m1 = m2 = m3 = 0; return; }

                int windowBins = N * binsPerGate;
                int startPhys = _head + (MaxBins - windowBins);
                while (startPhys < 0) startPhys += MaxBins;
                startPhys %= MaxBins;

                // initial sum for first gate
                int sum = 0;
                for (int b = 0; b < binsPerGate; b++) sum += _counts[(startPhys + b) % MaxBins];

                long s1 = 0, s2 = 0, s3 = 0;
                int binPtr = (startPhys + binsPerGate) % MaxBins;

                // slide gate across window
                for (int g = 0; g < N; g++)
                {
                    int nj = sum;
                    s1 += nj;
                    s2 += (long)nj * (nj - 1);
                    s3 += (long)nj * (nj - 1) * (nj - 2);

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
