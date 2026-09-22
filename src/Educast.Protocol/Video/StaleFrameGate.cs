namespace Educast.Protocol.Video;

/// <summary>
/// Client-side guard against out-of-order frames. Each video frame arrives on
/// its own unidirectional QUIC stream, and streams complete independently, so
/// a frame can surface after a newer one has already been rendered. Anything
/// not strictly newer than the newest already-delivered frame is discarded.
/// </summary>
public sealed class StaleFrameGate
{
    private uint? _newestAcceptedFrameId;

    /// <summary>Frames discarded for being stale, duplicate, or malformed.</summary>
    public int DroppedFrameCount { get; private set; }

    public bool TryAccept(uint frameId)
    {
        if (_newestAcceptedFrameId is { } newest && frameId <= newest)
        {
            DroppedFrameCount++;
            return false;
        }

        _newestAcceptedFrameId = frameId;
        return true;
    }

    /// <summary>
    /// Splits one completed video stream's payload into its envelope and the
    /// encoded frame bytes, accepting it only if it is newer than the newest
    /// frame already delivered.
    /// </summary>
    public bool TryAccept(ReadOnlyMemory<byte> streamPayload, out VideoFrameEnvelope envelope, out ReadOnlyMemory<byte> encodedFrame)
    {
        envelope = default;
        encodedFrame = default;

        if (streamPayload.Length < VideoFrameEnvelope.Size)
        {
            DroppedFrameCount++;
            return false;
        }

        var parsed = VideoFrameEnvelope.ReadFrom(streamPayload.Span);
        if (!TryAccept(parsed.FrameId))
        {
            return false;
        }

        envelope = parsed;
        encodedFrame = streamPayload[VideoFrameEnvelope.Size..];
        return true;
    }
}
