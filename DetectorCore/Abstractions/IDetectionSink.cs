namespace Listen_N.DetectorCore.Abstractions;

public interface IDetectionSink
{
    void OnDetection(DetectionCore.Protocol.DetectionEvent detectionEvent);
}
