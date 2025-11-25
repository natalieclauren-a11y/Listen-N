// Represents a summary log of an update cycle,
// containing various statistics, estimates, and diagnostic flags.

namespace Listen_N
{
    public sealed class UpdateCycleLog
    {
        public DateTime Timestamp { get; init; }   // Timestamp of the cycle
        public string State { get; init; }         // Current state identifier
        public int Tg { get; init; }                // Gate width in microseconds (µs)
        public double W { get; init; }              // Window duration in seconds (s)
        public int N { get; init; }                 // Number of gates counted
        public double M1 { get; init; }             // First moment (mean) aggregate
        public double M2 { get; init; }             // Second moment aggregate
        public double M3 { get; init; }             // Third moment aggregate
        public double Yhat { get; init; }           // Variance-to-mean estimate
        public double SigmaY { get; init; }         // Standard deviation of Yhat
        public double SigmaM1 { get; init; }        // Calculated sigma for M1
        public double TauHat { get; init; }         // Estimated gate time in ms
        public double Zy { get; init; }             // Normalized estimate (Yhat / SigmaY)

        // Diagnostic flags:
        public bool LowRate { get; init; }          // True if measured rate is very low (M1 < 1)
        public bool StatsBound { get; init; }       // True if sample size is too small (N < 2)
        public bool ModelMismatch { get; init; }    // True if Yhat is NaN, indicating model error
        public bool CovarianceFailure { get; init; }// True if SigmaY is not finite, indicating failure
        public bool Deadtime { get; init; }         // Placeholder flag for detector deadtime condition
    }
}