namespace Educast.MediaPipeline.Capture;

public interface IScreenCapture : IDisposable
{
    /// <summary>
    /// Waits up to <paramref name="timeout"/> for the screen or the mouse pointer to change and, if
    /// either did, returns the current image with the pointer drawn in. Returns <c>false</c> when
    /// nothing changed within the timeout, or when the display was just reset (the capture recovers
    /// by itself; the next call reports the new size). The previous frame's texture is invalid once
    /// this is called.
    /// </summary>
    bool TryGetNextFrame(TimeSpan timeout, out CapturedFrame frame);
}
