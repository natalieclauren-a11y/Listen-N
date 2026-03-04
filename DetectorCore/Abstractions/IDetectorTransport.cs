namespace Listen_N.DetectorCore.Abstractions;

public interface IDetectorTransport
{
    bool IsConnected { get; }
    void Send(ReadOnlySpan<byte> bytes);
}
