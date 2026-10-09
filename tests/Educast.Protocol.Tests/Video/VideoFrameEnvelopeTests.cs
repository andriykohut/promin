using Educast.Protocol.Video;

namespace Educast.Protocol.Tests.Video;

public class VideoFrameEnvelopeTests
{
    [Theory]
    [InlineData(0u, false)]
    [InlineData(1u, true)]
    [InlineData(uint.MaxValue, true)]
    [InlineData(uint.MaxValue, false)]
    [InlineData(0u, true)]
    public void WriteThenRead_RoundTripsFrameIdAndKeyframeFlag(uint frameId, bool isKeyframe)
    {
        var original = new VideoFrameEnvelope(frameId, isKeyframe);

        var bytes = new byte[VideoFrameEnvelope.Size];
        original.WriteTo(bytes);

        Assert.Equal(original, VideoFrameEnvelope.ReadFrom(bytes));
    }

    [Fact]
    public void WriteTo_LeavesTheEncodedFrameBytesThatFollowUntouched()
    {
        byte[] encodedFrame = [10, 20, 30];
        var payload = new byte[VideoFrameEnvelope.Size + encodedFrame.Length];

        new VideoFrameEnvelope(FrameId: 7, IsKeyframe: false).WriteTo(payload);
        encodedFrame.CopyTo(payload.AsSpan(VideoFrameEnvelope.Size));

        var decoded = VideoFrameEnvelope.ReadFrom(payload);

        Assert.Equal(7u, decoded.FrameId);
        Assert.False(decoded.IsKeyframe);
        Assert.Equal(encodedFrame, payload[VideoFrameEnvelope.Size..]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public void ReadFrom_ThrowsArgumentException_WhenBufferIsShorterThanFourBytes(int length)
    {
        // Below this brief's own fix threshold (BinaryPrimitives.ReadUInt32BigEndian
        // would otherwise throw ArgumentOutOfRangeException here) — pins that the
        // documented guard covers this range too, not just the boundary case below.
        var tooShort = new byte[length];

        var ex = Assert.Throws<ArgumentException>(() => VideoFrameEnvelope.ReadFrom(tooShort));
        Assert.Contains(VideoFrameEnvelope.Size.ToString(), ex.Message);
        Assert.Contains(length.ToString(), ex.Message);
    }

    [Fact]
    public void ReadFrom_ThrowsArgumentException_WhenBufferIsExactlyFourBytes()
    {
        // Exactly one byte short of Size: without the explicit guard this is
        // the case that instead throws IndexOutOfRangeException from
        // source[4], a different exception type than a too-short-by-more
        // buffer produces. The guard must unify both into one documented type.
        var fourBytes = new byte[4];

        var ex = Assert.Throws<ArgumentException>(() => VideoFrameEnvelope.ReadFrom(fourBytes));
        Assert.Contains(VideoFrameEnvelope.Size.ToString(), ex.Message);
        Assert.Contains("4", ex.Message);
    }

    [Fact]
    public void ReadFrom_Succeeds_WhenBufferIsExactlySizeBytes()
    {
        var exact = new byte[VideoFrameEnvelope.Size];
        new VideoFrameEnvelope(FrameId: 99, IsKeyframe: true).WriteTo(exact);

        var decoded = VideoFrameEnvelope.ReadFrom(exact);

        Assert.Equal(99u, decoded.FrameId);
        Assert.True(decoded.IsKeyframe);
    }
}
