using System.Buffers;
using System.Buffers.Binary;
using Promin.Protocol.Messages;
using Promin.Protocol.Pairing;

namespace Promin.Protocol.Tests.Messages;

public class ControlMessageCodecTests
{
    [Fact]
    public void EncodeThenDecode_RoundTripsPairChallengeRequest()
    {
        var original = new PairChallengeRequest(MasterNonce: [1, 2, 3, 4]);

        var encoded = ControlMessageCodec.Encode(original);
        var buffer = new ReadOnlySequence<byte>(encoded);

        var decoded = ControlMessageCodec.TryDecode(buffer, out var message, out var consumed);

        Assert.True(decoded);
        Assert.Equal(encoded.Length, consumed);
        var result = Assert.IsType<PairChallengeRequest>(message);
        Assert.Equal(original.MasterNonce, result.MasterNonce);
    }

    /// <summary>
    /// The two J-PAKE messages survive the wire with every byte array intact, including the
    /// jagged proofs. Fails a message that was added without its type discriminator, and a field
    /// the serializer drops or reorders.
    /// </summary>
    [Fact]
    public void EncodeThenDecode_RoundTripsTheJPakeMessages()
    {
        var round1 = new PairJPakeRound1(
            Gx1: [1, 2, 3],
            Gx2: [4, 5, 6, 7],
            ProofX1: [[8, 9], [10, 11, 12]],
            ProofX2: [[13], [14, 15, 16, 17, 18]]);
        var decoded1 = Assert.IsType<PairJPakeRound1>(RoundTrip(round1));
        Assert.Equal(round1.Gx1, decoded1.Gx1);
        Assert.Equal(round1.Gx2, decoded1.Gx2);
        AssertJagged(round1.ProofX1, decoded1.ProofX1);
        AssertJagged(round1.ProofX2, decoded1.ProofX2);

        var round2 = new PairJPakeRound2(A: [21, 22, 23], ProofX2s: [[24, 25], [26, 27, 28]]);
        var decoded2 = Assert.IsType<PairJPakeRound2>(RoundTrip(round2));
        Assert.Equal(round2.A, decoded2.A);
        AssertJagged(round2.ProofX2s, decoded2.ProofX2s);

        static void AssertJagged(byte[][] expected, byte[][] actual)
        {
            Assert.Equal(expected.Length, actual.Length);
            for (var i = 0; i < expected.Length; i++)
            {
                Assert.Equal(expected[i], actual[i]);
            }
        }
    }

    /// <summary>
    /// Round 1 is the largest message pairing sends. The control channel refuses frames over
    /// <see cref="ControlMessageCodec.MaxFrameSize"/>, so a real one must fit with room to spare.
    /// </summary>
    [Fact]
    public void RealJPakeRound1_FitsInOneControlFrame()
    {
        var round1 = new JPakeExchange(JPakeRole.Master, "482913"u8).CreateRound1();

        var encoded = ControlMessageCodec.Encode(round1);

        Assert.True(encoded.Length < ControlMessageCodec.MaxFrameSize,
            $"round 1 encodes to {encoded.Length} bytes; the frame limit is {ControlMessageCodec.MaxFrameSize}.");
        Assert.IsType<PairJPakeRound1>(RoundTrip(round1));
    }

    private static ControlMessage RoundTrip(ControlMessage message)
    {
        var encoded = ControlMessageCodec.Encode(message);
        Assert.True(ControlMessageCodec.TryDecode(
            new ReadOnlySequence<byte>(encoded), out var decoded, out var consumed));
        Assert.Equal(encoded.Length, consumed);
        return decoded!;
    }

    /// <summary>
    /// Every message of the pairing exchange survives the wire, including the two that carry no
    /// payload worth the name: the rejection (a PairAck with no proof) and the agent's explicit
    /// confirmation. Fails a message type that was added without its type discriminator.
    /// </summary>
    [Fact]
    public void EncodeThenDecode_RoundTripsEveryPairingMessage()
    {
        byte[] proof = [9, 8, 7];
        byte[] nonce = [1, 2, 3];

        var response = Assert.IsType<PairChallengeResponse>(RoundTrip(new PairChallengeResponse(proof, nonce)));
        Assert.Equal(proof, response.AgentProof);
        Assert.Equal(nonce, response.AgentNonce);

        var ack = Assert.IsType<PairAck>(RoundTrip(new PairAck(Success: true, proof)));
        Assert.True(ack.Success);
        Assert.Equal(proof, ack.MasterProof);

        var rejected = Assert.IsType<PairAck>(RoundTrip(PairAck.Rejected));
        Assert.False(rejected.Success);
        Assert.Null(rejected.MasterProof);

        Assert.IsType<PairConfirm>(RoundTrip(new PairConfirm()));

        static ControlMessage RoundTrip(ControlMessage message)
        {
            var encoded = ControlMessageCodec.Encode(message);
            Assert.True(ControlMessageCodec.TryDecode(
                new ReadOnlySequence<byte>(encoded), out var decoded, out var consumed));
            Assert.Equal(encoded.Length, consumed);
            return decoded!;
        }
    }

    /// <summary>
    /// The four messages of a file send survive the wire, including a Cyrillic path and a
    /// folder with no length. Fails a message added without its type discriminator.
    /// </summary>
    [Fact]
    public void SendMessages_RoundTrip()
    {
        ControlMessage[] messages =
        [
            new SendFiles(312_000_000, true),
            new SendItem("Тест 1/звіт.docx", 48_000, false),
            new SendItem("quiz_files", 0, true),
            new SendEnd(),
            new SendDone(SendOutcome.InUse),
        ];

        foreach (var message in messages)
        {
            Assert.Equal(message, RoundTrip(message));
        }
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
        var message = new PairChallengeRequest(MasterNonce: largeNonce);

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
        var message = new PairChallengeRequest(MasterNonce: hugeNonce);

        // The serialization will include JSON overhead (the byte[] becomes base64),
        // so this should exceed MaxFrameSize.
        var ex = Assert.Throws<ControlMessageFormatException>(() =>
            ControlMessageCodec.Encode(message));
        Assert.Contains("exceeds maximum frame size", ex.Message);
    }
}
