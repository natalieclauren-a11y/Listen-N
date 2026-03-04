namespace Listen_N.DetectorCore.Protocol;

public readonly record struct DetectionEvent(long TimestampUs, byte ChannelId);
