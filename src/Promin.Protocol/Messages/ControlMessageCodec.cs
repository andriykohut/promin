using System.Buffers;
using System.Buffers.Binary;
using System.Text.Json;

namespace Promin.Protocol.Messages;

public static class ControlMessageCodec
{
    /// <summary>
    /// Maximum size of a control message payload in bytes.
    /// Control messages carry nonces, HMACs, and a few small ints; 64 KB is generous and prevents unbounded buffering.
    /// </summary>
    public const int MaxFrameSize = 64 * 1024;

    public static byte[] Encode(ControlMessage message)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(message);

        if (payload.Length > MaxFrameSize)
        {
            throw new ControlMessageFormatException(
                $"Encoded message size {payload.Length} exceeds maximum frame size {MaxFrameSize}.");
        }

        var framed = new byte[4 + payload.Length];
        BinaryPrimitives.WriteInt32BigEndian(framed, payload.Length);
        payload.CopyTo(framed.AsSpan(4));
        return framed;
    }

    /// <summary>
    /// Attempts to decode a control message from a buffer.
    /// </summary>
    /// <param name="buffer">Buffer containing potential message frame(s).</param>
    /// <param name="message">Decoded message, or null if no complete frame is available.</param>
    /// <param name="consumedBytes">Bytes consumed from buffer; 0 if incomplete.</param>
    /// <returns>
    /// <c>true</c> if a message was successfully decoded.
    /// <c>false</c> if the buffer is incomplete (need more bytes).
    /// Throws <see cref="ControlMessageFormatException"/> if the frame is definitively corrupt.
    /// </returns>
    /// <exception cref="ControlMessageFormatException">
    /// Frame has a negative length prefix, length exceeds <see cref="MaxFrameSize"/>,
    /// payload is invalid JSON, $type discriminator is missing or unrecognized,
    /// or payload deserializes to null.
    /// </exception>
    public static bool TryDecode(ReadOnlySequence<byte> buffer, out ControlMessage? message, out long consumedBytes)
    {
        message = null;
        consumedBytes = 0;

        if (buffer.Length < 4)
        {
            return false;
        }

        Span<byte> lengthBytes = stackalloc byte[4];
        buffer.Slice(0, 4).CopyTo(lengthBytes);
        var payloadLength = BinaryPrimitives.ReadInt32BigEndian(lengthBytes);

        // Reject negative lengths
        if (payloadLength < 0)
        {
            throw new ControlMessageFormatException(
                $"Negative payload length {payloadLength} in frame header.");
        }

        // Reject lengths exceeding maximum frame size
        if (payloadLength > MaxFrameSize)
        {
            throw new ControlMessageFormatException(
                $"Payload length {payloadLength} exceeds maximum frame size {MaxFrameSize}.");
        }

        // Check if we have the complete frame (use long arithmetic to avoid overflow)
        long totalFrameSize = 4L + payloadLength;
        if (buffer.Length < totalFrameSize)
        {
            return false;
        }

        var payload = buffer.Slice(4, payloadLength).ToArray();

        // Attempt deserialization; catch and wrap any JSON errors or missing/invalid discriminators
        ControlMessage? decoded;
        try
        {
            decoded = JsonSerializer.Deserialize<ControlMessage>(payload);
        }
        catch (JsonException ex)
        {
            throw new ControlMessageFormatException(
                $"Failed to deserialize control message: {ex.Message}", ex);
        }
        catch (NotSupportedException ex)
        {
            // Missing or unrecognized type discriminator
            throw new ControlMessageFormatException(
                $"Failed to deserialize control message: {ex.Message}", ex);
        }

        // Reject null deserialization results
        if (decoded == null)
        {
            throw new ControlMessageFormatException(
                "Deserialized control message is null.");
        }

        message = decoded;
        consumedBytes = totalFrameSize;
        return true;
    }
}
