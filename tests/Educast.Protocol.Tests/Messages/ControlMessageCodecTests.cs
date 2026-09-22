using System.Buffers;
using System.Buffers.Binary;
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

    // Hostile-input tests: verify that corrupt frames are rejected with ControlMessageFormatException
    // and not left to silently fail or cause unbounded buffering.

    [Fact]
    public void TryDecode_ThrowsControlMessageFormatException_WhenLengthPrefixIsNegative()
    {
        // 0x80000000 as big-endian = -2147483648 as signed int
        var hostile = new byte[] { 0x80, 0x00, 0x00, 0x00, 0x7b, 0x7d };
        var buffer = new ReadOnlySequence<byte>(hostile);

        var ex = Assert.Throws<ControlMessageFormatException>(() =>
            ControlMessageCodec.TryDecode(buffer, out _, out _));
        Assert.Contains("Negative", ex.Message);
    }

    [Fact]
    public void TryDecode_ThrowsControlMessageFormatException_WhenLengthNearIntMaxValueWouldOverflow()
    {
        // int.MaxValue (0x7FFFFFFF) = 2147483647 bytes
        // 4 + 2147483647 would overflow in 32-bit arithmetic and exceeds MaxFrameSize
        var sizeBytes = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(sizeBytes, int.MaxValue);
        var hostile = new byte[5];
        Array.Copy(sizeBytes, hostile, 4);
        hostile[4] = 0x7b;
        var buffer = new ReadOnlySequence<byte>(hostile);

        var ex = Assert.Throws<ControlMessageFormatException>(() =>
            ControlMessageCodec.TryDecode(buffer, out _, out _));
        Assert.Contains("exceeds maximum frame size", ex.Message);
    }

    [Fact]
    public void TryDecode_ThrowsControlMessageFormatException_WhenLengthExceedsMaxFrameSize()
    {
        // MaxFrameSize + 1
        var sizeBytes = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(sizeBytes, ControlMessageCodec.MaxFrameSize + 1);
        var hostile = new byte[5];
        Array.Copy(sizeBytes, hostile, 4);
        hostile[4] = 0x7b;
        var buffer = new ReadOnlySequence<byte>(hostile);

        var ex = Assert.Throws<ControlMessageFormatException>(() =>
            ControlMessageCodec.TryDecode(buffer, out _, out _));
        Assert.Contains("exceeds maximum frame size", ex.Message);
    }

    [Fact]
    public void TryDecode_ThrowsControlMessageFormatException_WhenPayloadIsInvalidJson()
    {
        // Valid length prefix but invalid JSON payload
        var sizeBytes = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(sizeBytes, 5);
        var hostile = new byte[9];
        Array.Copy(sizeBytes, hostile, 4);
        hostile[4] = (byte)'n';
        hostile[5] = (byte)'o';
        hostile[6] = (byte)'t';
        hostile[7] = (byte)'j';
        hostile[8] = (byte)'s';
        var buffer = new ReadOnlySequence<byte>(hostile);

        var ex = Assert.Throws<ControlMessageFormatException>(() =>
            ControlMessageCodec.TryDecode(buffer, out _, out _));
        Assert.Contains("Failed to deserialize", ex.Message);
    }

    [Fact]
    public void TryDecode_ThrowsControlMessageFormatException_WhenTypeDiscriminatorIsMissing()
    {
        // Valid JSON but no $type field
        var json = "{}"u8.ToArray();
        var sizeBytes = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(sizeBytes, json.Length);
        var hostile = new byte[4 + json.Length];
        Array.Copy(sizeBytes, hostile, 4);
        Array.Copy(json, 0, hostile, 4, json.Length);
        var buffer = new ReadOnlySequence<byte>(hostile);

        var ex = Assert.Throws<ControlMessageFormatException>(() =>
            ControlMessageCodec.TryDecode(buffer, out _, out _));
        Assert.Contains("Failed to deserialize", ex.Message);
    }

    [Fact]
    public void TryDecode_ThrowsControlMessageFormatException_WhenTypeDiscriminatorIsUnrecognized()
    {
        // Valid JSON with unrecognized $type
        var json = """{"$type":"unknownType"}"""u8.ToArray();
        var sizeBytes = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(sizeBytes, json.Length);
        var hostile = new byte[4 + json.Length];
        Array.Copy(sizeBytes, hostile, 4);
        Array.Copy(json, 0, hostile, 4, json.Length);
        var buffer = new ReadOnlySequence<byte>(hostile);

        var ex = Assert.Throws<ControlMessageFormatException>(() =>
            ControlMessageCodec.TryDecode(buffer, out _, out _));
        Assert.Contains("Failed to deserialize", ex.Message);
    }

    [Fact]
    public void TryDecode_ThrowsControlMessageFormatException_WhenPayloadIsLiteralJsonNull()
    {
        // Literal JSON null: valid JSON that deserializes to null
        var json = "null"u8.ToArray();
        var sizeBytes = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(sizeBytes, json.Length);
        var hostile = new byte[4 + json.Length];
        Array.Copy(sizeBytes, hostile, 4);
        Array.Copy(json, 0, hostile, 4, json.Length);
        var buffer = new ReadOnlySequence<byte>(hostile);

        var ex = Assert.Throws<ControlMessageFormatException>(() =>
            ControlMessageCodec.TryDecode(buffer, out _, out _));
        Assert.Contains("Deserialized control message is null", ex.Message);
    }

    [Fact]
    public void TryDecode_AcceptsFrameJustUnderMaxFrameSize()
    {
        // Verify that a frame just under MaxFrameSize is accepted, not rejected off-by-one.
        // Create a large nonce that leaves room for JSON overhead (base64 encoding).
        // A 40 KB nonce + JSON overhead fits within 64 KB MaxFrameSize.
        var largeNonce = new byte[40 * 1024];
        var message = new PairChallengeRequest(Nonce: largeNonce);

        var encoded = ControlMessageCodec.Encode(message);
        Assert.True(encoded.Length <= ControlMessageCodec.MaxFrameSize,
            $"Encoded message size {encoded.Length} exceeds MaxFrameSize {ControlMessageCodec.MaxFrameSize}");

        var buffer = new ReadOnlySequence<byte>(encoded);
        var decoded = ControlMessageCodec.TryDecode(buffer, out var result, out _);

        Assert.True(decoded);
        Assert.NotNull(result);
        Assert.IsType<PairChallengeRequest>(result);
    }

    [Fact]
    public void Encode_ThrowsControlMessageFormatException_WhenSerializedMessageExceedsMaxFrameSize()
    {
        // Verify the defensive check exists: if a message ever serializes beyond MaxFrameSize,
        // fail loudly rather than emitting an invalid frame.
        // This is difficult to trigger naturally with built-in messages, but the guard is present.

        // Create a message with a very large nonce
        var hugeNonce = new byte[ControlMessageCodec.MaxFrameSize];
        var message = new PairChallengeRequest(Nonce: hugeNonce);

        // The serialization will include JSON overhead (the byte[] becomes base64),
        // so this should exceed MaxFrameSize.
        var ex = Assert.Throws<ControlMessageFormatException>(() =>
            ControlMessageCodec.Encode(message));
        Assert.Contains("exceeds maximum frame size", ex.Message);
    }
}
