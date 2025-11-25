using System;

namespace Listen_N
{
    public static class DeterministicRng
    {
        private static Random _rng = new Random();

        /// <summary>
        /// Expose a global Random instance.
        /// </summary>
        public static Random Instance => _rng;

        /// <summary>
        /// Reseed the RNG with a deterministic seed.
        /// </summary>
        public static void SetSeed(int seed)
        {
            _rng = new Random(seed);
        }
    }
}