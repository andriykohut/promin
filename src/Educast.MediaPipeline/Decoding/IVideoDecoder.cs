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
    /// Decodes one picture. Returns true and the picture when one came out of this call (a decoder set up for
    /// low delay returns the picture it was just given). Returns false, and throws nothing, when there is no
    /// picture: the stream has not started (a decoder can only start at a keyframe that carries its parameter
    /// sets, so until one arrives every other picture is refused), the bytes are not valid H.264, or the
    /// decoder could not decode them. After a failure the decoder refuses everything until the next keyframe,
    /// and then carries on: the caller asks the teacher for one.
    /// <paramref name="frame"/> is valid until the next call to this method or until <see cref="IDisposable.Dispose"/>
    /// (see <see cref="DecodedFrame"/>).
    /// </summary>
    bool TryDecode(EncodedFrame encoded, out DecodedFrame frame);
}
