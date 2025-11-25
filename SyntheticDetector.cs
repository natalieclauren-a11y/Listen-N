// SyntheticDetector simulates detection events with randomized timing and tube IDs,
// sending them asynchronously to an AdaptiveWindowEngine instance.
// It runs a background task generating these synthetic events until disposed.

using System;
using System.Threading;
using System.Threading.Tasks;

namespace Listen_N
{
    // Generates synthetic detection events and feeds them to AdaptiveWindowEngine.
    // Runs in background until disposed.
    public sealed class SyntheticDetector : IDisposable
    {
        private readonly AdaptiveWindowEngine _adaptive;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _worker;
        private readonly Random _rng = new();

        public SyntheticDetector(AdaptiveWindowEngine adaptive)
        {
            _adaptive = adaptive ?? throw new ArgumentNullException(nameof(adaptive));
            _worker = Task.Run(() => GenerateAsync(_cts.Token));
        }

        // Generates detection events with random intervals and tube IDs until cancelled.
        private async Task GenerateAsync(CancellationToken token)
        {
            long tUs = _adaptive.LeftEdgeUs + 1;

            while (!token.IsCancellationRequested)
            {
                tUs += _rng.Next(50, 500);
                byte tubeId = (byte)_rng.Next(0, 15);

                _adaptive.OnDetection(new Detection(tUs, tubeId));
                Console.WriteLine($"Synthetic event at {tUs} µs, tube {tubeId}");

                await Task.Delay(1, token).ConfigureAwait(false);
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            try { _worker.Wait(); } catch { }
            _cts.Dispose();
        }
    }
}