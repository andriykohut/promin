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

        var decoded = VideoFrameEnvelope.ReadFrom(original.ToArray());

        Assert.Equal(original, decoded);
    }

    [Fact]
    public void ToArray_ProducesExactlyEnvelopeSizeBytes()
    {
        var bytes = new VideoFrameEnvelope(FrameId: 42, IsKeyframe: true).ToArray();

        Assert.Equal(VideoFrameEnvelope.Size, bytes.Length);
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
}
