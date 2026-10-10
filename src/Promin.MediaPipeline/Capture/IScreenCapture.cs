namespace Promin.MediaPipeline.Capture;

public interface IScreenCapture : IDisposable
{
    /// <summary>
    /// Waits up to <paramref name="timeout"/> for the screen or the mouse pointer to change and, if
    /// either did, returns the current image with the pointer drawn in. Returns <c>false</c> when
    /// nothing changed within the timeout, or when the display was just reset (the capture recovers
    /// by itself; the next call reports the new size). The frame's texture holds the picture until
    /// the next call that returns <c>true</c>, which rewrites or replaces it, or until <see cref="IDisposable.Dispose"/>.
    /// </summary>
    bool TryGetNextFrame(TimeSpan timeout, out CapturedFrame frame);
}
