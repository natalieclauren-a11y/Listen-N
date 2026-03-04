using Listen_N.DetectorCore.Protocol;

namespace Listen_N.Tests;

public class Test_DetectorProtocolParser
{
    private static byte[] EncodeWord(long timestampUs, byte channelId)
    {
        ulong ticks10Ns = (ulong)timestampUs * 100UL;
        ulong word = (ticks10Ns << 5) | (ulong)(channelId & 0x1F);
        return BitConverter.GetBytes(word);
    }

    [Fact]
    public void DecodeEventWord_DecodesKnownPattern_ZeroTimestamp()
    {
        byte[] bytes = EncodeWord(0, 17);

        DetectionEvent decoded = DetectorProtocolParser.DecodeEventWord(bytes);

        Assert.Equal(0, decoded.TimestampUs);
        Assert.Equal((byte)17, decoded.ChannelId);
    }

    [Fact]
    public void DecodeEventWord_DecodesKnownPattern_NonZeroTimestamp()
    {
        byte[] bytes = EncodeWord(123_456, 3);

        DetectionEvent decoded = DetectorProtocolParser.DecodeEventWord(bytes);

        Assert.Equal(123_456, decoded.TimestampUs);
        Assert.Equal((byte)3, decoded.ChannelId);
    }

    [Fact]
    public void DecodeEventWord_RejectsInvalidLength()
    {
        byte[] bytes = new byte[7];

        Assert.Throws<ArgumentException>(() => DetectorProtocolParser.DecodeEventWord(bytes));
    }

    [Fact]
    public void DecodeEventWord_SmokeParseSequence()
    {
        byte[] stream =
        [
            ..EncodeWord(10, 1),
            ..EncodeWord(20, 2),
            ..EncodeWord(30, 3)
        ];

        var events = new List<DetectionEvent>();
        for (int i = 0; i + sizeof(ulong) <= stream.Length; i += sizeof(ulong))
        {
            events.Add(DetectorProtocolParser.DecodeEventWord(stream.AsSpan(i, sizeof(ulong))));
        }

        Assert.Equal(3, events.Count);
        Assert.Collection(events,
            e => { Assert.Equal(10, e.TimestampUs); Assert.Equal((byte)1, e.ChannelId); },
            e => { Assert.Equal(20, e.TimestampUs); Assert.Equal((byte)2, e.ChannelId); },
            e => { Assert.Equal(30, e.TimestampUs); Assert.Equal((byte)3, e.ChannelId); });
    }
}
