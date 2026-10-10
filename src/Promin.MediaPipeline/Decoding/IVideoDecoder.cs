using Promin.MediaPipeline.Encoding;

namespace Promin.MediaPipeline.Decoding;

/// <summary>
/// Decodes the pictures of one H.264 stream (Annex B, as <see cref="IVideoEncoder"/> produces it), one at a
/// time, in the order they were encoded. Not thread-safe: calls may come from different threads over time but
/// never at once.
/// </summary>
public interface IVideoDecoder : IDisposable
{
    /// <summary>
    /// Whether the decoder has nothing to build a picture on until a keyframe arrives. It only reports: nothing
    /// in the product acts on it, because the protocol has no way to ask the teacher PC for a keyframe (one is
    /// sent every two seconds regardless) and the student's session finds loss from the frame ids.
    /// True from construction and again
    /// after anything that breaks the stream (bytes that were refused, a decoder that refused or failed). It stays
    /// true until a picture has come out of a call made after a keyframe was accepted; accepting a keyframe is not
    /// enough, since a decoder can accept one and give nothing back. A decoder that holds one picture back
    /// reports true for one call longer. Once it has gone false, a later keyframe that is accepted but gives no
    /// picture does not set it again: the two cases (a picture held back, a picture swallowed) cannot be told
    /// apart there, and re-arming would freeze a decoder that legitimately holds pictures. Non-keyframes are
    /// offered to the decoder as soon as a keyframe has been accepted, whatever this says.
    /// </summary>
    bool NeedsKeyframe { get; }

    /// <summary>
    /// Decodes one picture. Returns true and the picture when one came out of this call (a decoder set up for
    /// low delay returns the picture it was just given). Returns false, and throws nothing, when there is no
    /// picture: the stream has not started (a decoder can only start at a keyframe that carries its parameter
    /// sets, so until one arrives every other picture is refused), the framing is malformed (not an H.264 Annex B
    /// byte stream), the decoder refused or failed, or the decoder gave nothing back (held the picture back, or swallowed
    /// the input). After a refusal or failure <see cref="NeedsKeyframe"/> is true. A call can also return true and
    /// leave <see cref="NeedsKeyframe"/> true: the picture came out and then the stream broke. Damage inside well-formed framing is the decoder's to
    /// conceal: it may return a smeared picture and true, so loss is for the session to detect from frame ids.
    /// <paramref name="frame"/> is valid until the next call to this method or until <see cref="IDisposable.Dispose"/>
    /// (see <see cref="DecodedFrame"/>).
    /// </summary>
    bool TryDecode(EncodedFrame encoded, out DecodedFrame frame);
}
