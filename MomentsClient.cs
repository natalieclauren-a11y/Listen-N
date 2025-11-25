// MomentsClient.cs
// Provides a client interface to query and process statistical moment estimates from a Detector.
// Interfaces with AdaptiveWindowEngine to request moment data (m1..m8) for various gate widths asynchronously.
// Stores and updates moment samples, allowing retrieval of latest data per gate.
//
// Usage:
// - LoadGateWidthsAsync to populate available gate widths.
// - QueryGateAsync to get moment data for a specific gate index.
// - OnFeynmanArrived should be called when new Feynman moment data arrives asynchronously.
// - TryGetMoments to get the latest cached moments for a gate.
//
// Threading:
// - Uses async/await for non-blocking operations.
// - ConcurrentDictionary used for thread-safe storage of latest moments.
// - Incoming data callback (OnFeynmanArrived) may be invoked from background threads.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Vf61Gui.Moments
{
    // Represents one moment sample with gate width and moment values (m1..m8).
    public class MomentsSample
    {
        public double GateMicroseconds { get; set; }
        public double[] M { get; set; } = new double[8];
    }

    public class MomentsClient
    {
        private readonly Detector _detector;
        private double[] _gateUs;

        public MomentsClient(Detector detector)
        {
            _detector = detector ?? throw new ArgumentNullException(nameof(detector));
        }

        /// <summary>
        /// Queries the detector for all available gate widths (default 16),
        /// storing them internally as microseconds.
        /// </summary>
        public async Task<double[]> LoadGateWidthsAsync(int numOfGatewidths = 16, int delayPerGateMs = 50)
        {
            _detector.feynmans.Clear();

            for (int i = 0; i < numOfGatewidths; i++)
            {
                _detector.WriteAndWait($"Moments = {i}");
                await Task.Delay(delayPerGateMs);
            }

            await Task.Delay(200); // wait for any late responses

            _gateUs = _detector.feynmans
                .OrderBy(f => f.gatewidth)
                .Select(f => f.gatewidth / 1000.0) // convert ns to µs
                .ToArray();

            return _gateUs;
        }

        /// <summary>
        /// Queries moment data (m1..m8) for a single gate index.
        /// Throws if gate widths not loaded or index out of range.
        /// </summary>
        public async Task<MomentsSample> QueryGateAsync(int index, int delayAfterMs = 100)
        {
            if (_gateUs == null || index < 0 || index >= _gateUs.Length)
                throw new InvalidOperationException("Gate widths not loaded or index out of range.");

            _detector.WriteAndWait($"Moments = {index}");
            await Task.Delay(delayAfterMs);

            var feyn = _detector.feynmans
                .FirstOrDefault(f => Math.Abs(f.gatewidth / 1000.0 - _gateUs[index]) < 1e-6);

            if (feyn == null)
                throw new InvalidOperationException($"No Feynman data for gate index {index}");

            return new MomentsSample
            {
                GateMicroseconds = _gateUs[index],
                M = feyn.feynmanMoments.m.ToArray()
            };
        }

        private readonly ConcurrentDictionary<int, double[]> _latestMoments = new();

        /// <summary>
        /// Updates cached moments when new Feynman data arrives.
        /// </summary>
        public void OnFeynmanArrived(Feynman f)
        {
            int us = (int)(f.gatewidth / 1000.0);
            _latestMoments[us] = f.feynmanMoments.m.ToArray();
        }

        /// <summary>
        /// Tries to retrieve latest cached moments for given gate index.
        /// Returns false if gate widths not loaded, index out of range, or no data.
        /// </summary>
        public bool TryGetMoments(int gateIndex, out MomentsSample sample)
        {
            sample = default;

            if (_gateUs == null || gateIndex < 0 || gateIndex >= _gateUs.Length)
                return false;

            int us = (int)_gateUs[gateIndex];
            if (_latestMoments.TryGetValue(us, out var m))
            {
                sample = new MomentsSample { GateMicroseconds = us, M = m };
                return true;
            }

            return false;
        }

        /// <summary>
        /// Logs adaptive window parameters and statistics to console.
        /// </summary>
        public static void PublishAdaptiveWindow(
            long nowUs,
            int gateUs,
            double windowSec,
            double m1,
            double y,
            double sigmaY,
            double zy,
            string state,
            bool hasSignificance)
        {
            Console.WriteLine(
                $"[Adaptive] t={nowUs} Tg={gateUs}us W={windowSec:F3}s " +
                $"m1={m1:F3} Y={y:F5} ±{sigmaY:F5} ZY={zy:F2} state={state} signif={hasSignificance}");
        }

        /// <summary>
        /// Queries moments for all loaded gates once.
        /// </summary>
        public async Task<List<MomentsSample>> QueryAllOnceAsync()
        {
            var results = new List<MomentsSample>();

            for (int i = 0; i < _gateUs.Length; i++)
            {
                results.Add(await QueryGateAsync(i));
            }

            return results;
        }
    }
}