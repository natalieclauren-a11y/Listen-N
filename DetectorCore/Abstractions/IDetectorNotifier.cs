namespace Listen_N.DetectorCore.Abstractions;

public interface IDetectorNotifier
{
    void NotifyError(string message, string? caption = null);
}
