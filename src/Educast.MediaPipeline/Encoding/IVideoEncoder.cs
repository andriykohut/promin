using Educast.MediaPipeline.Capture;

namespace Educast.MediaPipeline.Encoding;

public interface IVideoEncoder : IDisposable
{
    /// <summary>True when a hardware H.264 encoder was activated, false when Windows' software encoder is in use.</summary>
    bool UsingHardwareEncoder { get; }

    /// <summary>
    /// Encodes <paramref name="frame"/>. The frame's texture is only read for the duration of the call.
    /// When <paramref name="forceKeyframe"/> is set the frame is encoded as a keyframe (an IDR picture
    /// with its parameter sets), and the result says what was really produced.
    /// Returns false when no picture came out for this call (the encoder is not ready or fell behind).
    /// One call at a time; not thread-safe.
    /// </summary>
    bool TryEncode(CapturedFrame frame, bool forceKeyframe, out EncodedFrame encoded);
}
