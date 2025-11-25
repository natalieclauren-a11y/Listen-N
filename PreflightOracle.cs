// PreflightOracle processes input parameters to produce an UpdateCycleLog,
// which summarizes various statistics and flags for a given cycle.

namespace Listen_N
{
    public static class PreflightOracle
    {
        // Creates an UpdateCycleLog instance based on provided metrics and thresholds.
        //
        // Parameters:
        //  - timestamp: the current timestamp of the cycle
        //  - state: the current state as a string
        //  - tg: gate time (int), used as a proxy in milliseconds
        //  - w: weight or related metric (double)
        //  - n: sample count or observations number
        //  - m1, m2, m3: statistical moments or aggregates
        //  - yhat: predicted or estimated value
        //  - sigmaY: uncertainty or standard deviation associated with yhat
        //
        // Returns:
        //  - UpdateCycleLog with computed fields and diagnostic flags
        public static UpdateCycleLog Run(
            DateTime timestamp,
            string state,
            int tg,
            double w,
            int n,
            double m1,
            double m2,
            double m3,
            double yhat,
            double sigmaY)
        {
            double sigmaM1 = (n > 0 && m1 > 0) ? Math.Sqrt(m1 / n) : double.PositiveInfinity;
            double tauHat = tg / 1000.0; // Convert gate time to milliseconds

            return new UpdateCycleLog
            {
                Timestamp = timestamp,
                State = state,
                Tg = tg,
                W = w,
                N = n,
                M1 = m1,
                M2 = m2,
                M3 = m3,
                Yhat = yhat,
                SigmaY = sigmaY,
                SigmaM1 = sigmaM1,
                TauHat = tauHat,
                Zy = sigmaY > 0 ? yhat / sigmaY : 0,
                LowRate = m1 < 1.0,
                StatsBound = n < 2,
                ModelMismatch = double.IsNaN(yhat),
                CovarianceFailure = !double.IsFinite(sigmaY),
                Deadtime = false // Placeholder for potential integration with Detector.deadtime
            };
        }
    }
}