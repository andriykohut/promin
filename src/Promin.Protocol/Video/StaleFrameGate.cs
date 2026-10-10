namespace Promin.Protocol.Video;

/// <summary>
/// Client-side guard against out-of-order frames. Each video frame arrives on
/// its own unidirectional QUIC stream, and streams complete independently, so
/// a frame can surface after a newer one has already been rendered. Anything
/// not strictly newer than the newest already-delivered frame is discarded.
/// </summary>
public sealed class StaleFrameGate
{
    private uint? _newestAcceptedFrameId;

    /// <summary>
    /// Frames discarded for being stale, duplicate, or malformed, for the
    /// lifetime of this gate instance. This is a cumulative, session-wide
    /// counter: it is for the agent PC's own log (nothing carries it to the
    /// console, whose "dropped frames" come from the broadcast session), and it
    /// is intentionally NOT cleared by <see cref="Reset"/> — a log of a client
    /// that keeps needing resets (e.g. because the master keeps restarting
    /// broadcasts, or the client keeps reconnecting) needs the running total
    /// to stay visible, not silently zero itself out on every reset.
    /// </summary>
    public int DroppedFrameCount { get; private set; }

    /// <summary>
    /// Returns the gate to its initial sequencing state: there is no newest
    /// frame id yet, so the next call to <see cref="TryAccept(uint)"/> (or
    /// the payload overload) accepts whatever id it is given, and gating
    /// resumes from there. Call this whenever the underlying frame sequence
    /// itself has restarted — most notably when the client reconnects to a
    /// freshly (re)started <c>BroadcastSession</c>, whose frame counter
    /// begins again at 0. Without this, a gate left over from a prior
    /// broadcast would reject every frame of the new one forever, since none
    /// of them are numerically newer than the last frame of the old one.
    /// Does not clear <see cref="DroppedFrameCount"/> — see its remarks.
    /// </summary>
    public void Reset()
    {
        _newestAcceptedFrameId = null;
    }

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
