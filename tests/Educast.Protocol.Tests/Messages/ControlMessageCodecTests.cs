using System.Buffers;
using Educast.Protocol.Messages;

namespace Educast.Protocol.Tests.Messages;

public class ControlMessageCodecTests
{
    [Fact]
    public void EncodeThenDecode_RoundTripsPairChallengeRequest()
    {
        var original = new PairChallengeRequest(Nonce: [1, 2, 3, 4]);

        var encoded = ControlMessageCodec.Encode(original);
        var buffer = new ReadOnlySequence<byte>(encoded);

        var decoded = ControlMessageCodec.TryDecode(buffer, out var message, out var consumed);

        Assert.True(decoded);
        Assert.Equal(encoded.Length, consumed);
        var result = Assert.IsType<PairChallengeRequest>(message);
        Assert.Equal(original.Nonce, result.Nonce);
    }

    [Fact]
    public void TryDecode_ReturnsFalse_WhenBufferHasIncompleteFrame()
    {
        var encoded = ControlMessageCodec.Encode(new StartBroadcast(Width: 1920, Height: 1080, TargetFps: 15));
        var partial = new ReadOnlySequence<byte>(encoded.AsMemory(0, encoded.Length - 1).ToArray());

        var decoded = ControlMessageCodec.TryDecode(partial, out var message, out var consumed);

        Assert.False(decoded);
        Assert.Null(message);
        Assert.Equal(0, consumed);
    }

    [Fact]
    public void TryDecode_LeavesRemainderUnconsumed_WhenBufferHasTwoFrames()
    {
        var first = ControlMessageCodec.Encode(new StopBroadcast());
        var second = ControlMessageCodec.Encode(new BroadcastReady());
        var buffer = new ReadOnlySequence<byte>([.. first, .. second]);

        var decodedFirst = ControlMessageCodec.TryDecode(buffer, out var firstMessage, out var firstConsumed);
        var remainder = buffer.Slice(firstConsumed);
        var decodedSecond = ControlMessageCodec.TryDecode(remainder, out var secondMessage, out _);

        Assert.True(decodedFirst);
        Assert.IsType<StopBroadcast>(firstMessage);
        Assert.True(decodedSecond);
        Assert.IsType<BroadcastReady>(secondMessage);
    }
}
