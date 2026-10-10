using Vortice.Direct3D11;

namespace Promin.MediaPipeline.Capture;

/// <summary>
/// One captured desktop image, pointer included. <paramref name="Texture"/> belongs to the capture
/// that produced it: it holds this picture until the next <see cref="IScreenCapture.TryGetNextFrame"/>
/// call that returns a frame, or <see cref="IDisposable.Dispose"/>, and the consumer must not dispose it. It is a BGRA
/// default-usage texture with no CPU access, living on the capture's <c>Device</c>.
/// <paramref name="Width"/> and <paramref name="Height"/> are the display's current pixel size and
/// can differ from one frame to the next (a display mode change).
/// </summary>
public readonly record struct CapturedFrame(ID3D11Texture2D Texture, int Width, int Height);
