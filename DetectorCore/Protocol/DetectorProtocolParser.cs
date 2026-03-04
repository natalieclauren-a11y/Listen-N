using System.Buffers.Binary;

namespace Listen_N.DetectorCore.Protocol;

public static class DetectorProtocolParser
{
    public static DetectionEvent DecodeEventWord(ReadOnlySpan<byte> eightBytes)
    {
        if (eightBytes.Length != sizeof(ulong))
        {
            throw new ArgumentException("Detector event word must be exactly 8 bytes.", nameof(eightBytes));
        }

        ulong word = BinaryPrimitives.ReadUInt64LittleEndian(eightBytes);
        byte channelId = (byte)(word & 0x1F);
        long ticks10Ns = (long)(word >> 5);
        long timestampUs = ticks10Ns / 100;

        return new DetectionEvent(timestampUs, channelId);
    }
}
