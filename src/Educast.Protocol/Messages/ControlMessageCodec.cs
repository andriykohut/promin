using System.Buffers;
using System.Buffers.Binary;
using System.Text.Json;

namespace Educast.Protocol.Messages;

public static class ControlMessageCodec
{
    public static byte[] Encode(ControlMessage message)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(message);
        var framed = new byte[4 + payload.Length];
        BinaryPrimitives.WriteInt32BigEndian(framed, payload.Length);
        payload.CopyTo(framed.AsSpan(4));
        return framed;
    }

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

        if (buffer.Length < 4 + payloadLength)
        {
            return false;
        }

        var payload = buffer.Slice(4, payloadLength).ToArray();
        message = JsonSerializer.Deserialize<ControlMessage>(payload)
            ?? throw new InvalidOperationException("Decoded a null control message.");
        consumedBytes = 4 + payloadLength;
        return true;
    }
}
