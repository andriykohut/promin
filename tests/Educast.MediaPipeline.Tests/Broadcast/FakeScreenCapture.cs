using Educast.MediaPipeline.Capture;

namespace Educast.MediaPipeline.Tests.Broadcast;

/// <summary>
/// A screen capture that follows a script: each call either delivers a frame of the given size or
/// reports that nothing changed. Once the script is used up every call reports nothing changed.
/// The frames carry no texture; the adapter never touches it.
/// </summary>
internal sealed class FakeScreenCapture : IScreenCapture
{
    private readonly Queue<(int Width, int Height)?> _script = new();

    public int Calls { get; private set; }

    public bool Disposed { get; private set; }

    public FakeScreenCapture Frame(int width, int height)
    {
        _script.Enqueue((width, height));
        return this;
    }

    public FakeScreenCapture Still()
    {
        _script.Enqueue(null);
        return this;
    }

    public bool TryGetNextFrame(TimeSpan timeout, out CapturedFrame frame)
    {
        Calls++;
        frame = default;
        if (_script.Count == 0 || _script.Dequeue() is not { } size)
        {
            return false;
        }

        frame = new CapturedFrame(null!, size.Width, size.Height);
        return true;
    }

    public void Dispose() => Disposed = true;
}
