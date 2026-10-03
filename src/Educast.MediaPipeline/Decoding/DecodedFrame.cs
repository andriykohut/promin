using Vortice.Direct3D11;

namespace Educast.MediaPipeline.Decoding;

/// <summary>
/// One decoded picture, on the GPU, as an NV12 texture (a full-size luma plane, then one interleaved
/// half-height chroma plane; limited range, as the stream says).
/// <para>
/// <paramref name="Width"/> and <paramref name="Height"/> are the size of the picture, not of the surface it
/// was decoded into: H.264 codes in 16-pixel blocks, so a 1920x1080 picture lives in a 1920x1088 surface. The
/// picture is the top-left <paramref name="Width"/> by <paramref name="Height"/> pixels of
/// <paramref name="Texture"/>, which can be larger; whoever shows the frame must crop to that size, or a
/// band of undecoded rows shows along the bottom (and right) edge.
/// </para>
/// <para>
/// Lifetime follows the screen capture's rule: the decoder reuses its output, so a frame is valid until the
/// next <see cref="IVideoDecoder.TryDecode"/> call or until the decoder is disposed, whichever comes first.
/// The texture belongs to the decoder: the caller must not dispose it, keep it, or write to it. A caller that
/// needs the picture longer copies it (on the GPU) before the next call. The texture lives on the device the
/// decoder was given.
/// </para>
/// </summary>
public readonly record struct DecodedFrame(ID3D11Texture2D Texture, int Width, int Height);
