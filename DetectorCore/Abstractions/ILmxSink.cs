namespace Listen_N.DetectorCore.Abstractions;

public interface ILmxSink
{
    void WriteHeaderIfNeeded(string headerText);
    void WritePayload(ReadOnlySpan<byte> payload);
}
