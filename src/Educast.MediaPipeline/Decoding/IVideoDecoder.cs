using Educast.MediaPipeline.Encoding;

namespace Educast.MediaPipeline.Decoding;

/// <summary>
/// Decodes the pictures of one H.264 stream (Annex B, as <see cref="IVideoEncoder"/> produces it), one at a
/// time, in the order they were encoded. Not thread-safe: calls may come from different threads over time but
/// never at once.
/// </summary>
public interface IVideoDecoder : IDisposable
{
    /// <summary>
    /// True from construction until a keyframe has been decoded, and true again after anything that makes the
    /// next delta undecodable (bytes that were refused, a decoder that refused or failed). While it is true,
    /// everything but a keyframe is refused, so the caller asks the teacher for one. A picture the decoder merely
    /// held back does not set it.
    /// </summary>
    bool NeedsKeyframe { get; }

    /// <summary>
    /// Decodes one picture. Returns true and the picture when one came out of this call (a decoder set up for
    /// low delay returns the picture it was just given). Returns false, and throws nothing, when there is no
    /// picture: the stream has not started (a decoder can only start at a keyframe that carries its parameter
    /// sets, so until one arrives every other picture is refused), the framing is malformed (not an H.264 Annex B
    /// byte stream), the decoder refused or failed, or the decoder kept the picture back. After a refusal or
    /// failure <see cref="NeedsKeyframe"/> is true. Damage inside well-formed framing is the decoder's to
    /// conceal: it may return a smeared picture and true, so loss is for the session to detect from frame ids.
    /// <paramref name="frame"/> is valid until the next call to this method or until <see cref="IDisposable.Dispose"/>
    /// (see <see cref="DecodedFrame"/>).
    /// </summary>
    bool TryDecode(EncodedFrame encoded, out DecodedFrame frame);
}
