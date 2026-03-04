using System.Windows.Forms;

namespace Listen_N.DetectorCore.Abstractions;

public sealed class SystemDetectorClock : IDetectorClock
{
    public DateTime UtcNow => DateTime.UtcNow;
}

public sealed class NullDetectorTransport : IDetectorTransport
{
    public static readonly NullDetectorTransport Instance = new();
    private NullDetectorTransport() { }

    public bool IsConnected => false;

    public void Send(ReadOnlySpan<byte> bytes)
    {
        // Intentionally empty default for incremental migration.
    }
}

public sealed class WinFormsDetectorNotifier : IDetectorNotifier
{
    public void NotifyError(string message, string? caption = null)
    {
        MessageBox.Show(message, caption ?? "Detector", MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
    }
}

public sealed class NullDetectionSink : IDetectionSink
{
    public static readonly NullDetectionSink Instance = new();
    private NullDetectionSink() { }

    public void OnDetection(DetectionCore.Protocol.DetectionEvent detectionEvent)
    {
        // Intentionally empty default for incremental migration.
    }
}
