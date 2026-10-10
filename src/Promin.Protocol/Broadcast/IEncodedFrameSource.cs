namespace Promin.Protocol.Broadcast;

/// <summary>
/// Where <see cref="BroadcastSession"/> gets its encoded frames: screen capture
/// and video encoding behind one call, because the raw frame is a GPU texture
/// that never leaves the GPU and so cannot pass between two interfaces.
/// </summary>
public interface IEncodedFrameSource
{
    /// <summary>
    /// Produces the next encoded frame.
    /// Returns true with a frame when the picture changed since the last call, or when
    /// <paramref name="forceKeyframe"/> is true and a picture exists (the source then
    /// encodes the current picture again, as a keyframe).
    /// Returns false when nothing changed within <paramref name="timeout"/> and no
    /// keyframe was forced, or when no picture exists yet.
    /// </summary>
    bool TryGetNextFrame(TimeSpan timeout, bool forceKeyframe, out EncodedVideoFrame frame);
}

/// <summary>One encoded frame. <see cref="IsKeyframe"/> is what the encoder actually produced.</summary>
public readonly record struct EncodedVideoFrame(byte[] Data, bool IsKeyframe);
