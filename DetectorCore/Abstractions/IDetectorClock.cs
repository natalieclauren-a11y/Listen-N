namespace Listen_N.DetectorCore.Abstractions;

public interface IDetectorClock
{
    DateTime UtcNow { get; }
}
