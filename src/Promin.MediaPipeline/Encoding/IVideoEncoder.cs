using Promin.MediaPipeline.Capture;

namespace Promin.MediaPipeline.Encoding;

public interface IVideoEncoder : IDisposable
{
    /// <summary>True when a hardware H.264 encoder was activated, false when Windows' software encoder is in use.</summary>
    bool UsingHardwareEncoder { get; }

    /// <summary>
    /// Encodes <paramref name="frame"/>. The frame's texture is only read for the duration of the call.
    /// When <paramref name="forceKeyframe"/> is set the frame is encoded as a keyframe (an IDR picture), and
    /// the result says what was really produced: <c>IsKeyframe</c> comes from the bytes. Every keyframe carries
    /// its parameter sets (SPS and PPS); the call throws rather than hand out one that cannot.
    /// Returns the oldest finished picture, which is normally the one just fed. Returns false when none is
    /// ready: the encoder is slower than the call is willing to wait (the picture is not lost: it is returned
    /// by <see cref="TryTakePending"/> or by a later call), or it would not take the frame in time.
    /// A finished picture is returned even when the new frame was refused.
    /// One call at a time; not thread-safe.
    /// </summary>
    bool TryEncode(CapturedFrame frame, bool forceKeyframe, out EncodedFrame encoded);

    /// <summary>
    /// Returns a picture the encoder has finished but TryEncode has not yet handed out,
    /// without feeding it a new frame. False when nothing is ready.
    /// </summary>
    bool TryTakePending(out EncodedFrame encoded);
}
