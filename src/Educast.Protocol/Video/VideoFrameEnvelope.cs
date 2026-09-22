using System.Buffers.Binary;

namespace Educast.Protocol.Video;

/// <summary>
/// Written at the head of every per-frame unidirectional video stream,
/// immediately followed by the encoded frame bytes. One stream, read to end,
/// is exactly one envelope plus one whole encoded frame.
/// </summary>
public readonly record struct VideoFrameEnvelope(uint FrameId, bool IsKeyframe)
{
    public const int Size = 5;

    public void WriteTo(Span<byte> destination)
    {
        BinaryPrimitives.WriteUInt32BigEndian(destination, FrameId);
        destination[4] = (byte)(IsKeyframe ? 1 : 0);
    }

    public byte[] ToArray()
    {
        var buffer = new byte[Size];
        WriteTo(buffer);
        return buffer;
    }

    public static VideoFrameEnvelope ReadFrom(ReadOnlySpan<byte> source) => new(
        FrameId: BinaryPrimitives.ReadUInt32BigEndian(source),
        IsKeyframe: source[4] != 0);
}
