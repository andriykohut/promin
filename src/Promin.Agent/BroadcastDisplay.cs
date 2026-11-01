using Promin.MediaPipeline.Decoding;
using Promin.Protocol.Broadcast;
using Promin.Protocol.Messages;

namespace Promin.Agent;

/// <summary>Makes <see cref="BroadcastDisplay"/>s.</summary>
internal sealed class BroadcastDisplayFactory(bool windowed, Action<string> log, ScreenLock? screenLock) : IBroadcastDisplayFactory
{
    public Task<IBroadcastDisplay?> StartAsync(StartBroadcast message) => BroadcastDisplay.StartAsync(message, windowed, log, screenLock);
}

/// <summary>
/// One broadcast on the agent PC's screen, owning its own thread. On a PC given a <see cref="ScreenLock"/>, keyboard
/// and mouse are blocked from the moment the picture is up until it is gone, however it ends (see
/// <see cref="ScreenLock.PictureShown"/>); with none, input is left alone.
/// </summary>
/// <remarks>
/// <para>
/// <b>Threading.</b> Three kinds of thread meet here. <i>The display thread</i> is created for this display and
/// does everything that touches the window, the Direct3D device or the decoder: it creates them, pumps the
/// window's messages, decodes, draws, and at the end destroys the window and releases the rest, in that order.
/// Nothing else ever touches those objects, so a picture is never presented from a thread that races the window's
/// destruction: destruction is something the same thread does between two messages. <i>The receive loop</i>
/// (<see cref="BroadcastConnection"/>'s pump, on the thread pool) only puts a frame's bytes into a small bounded
/// mailbox and posts one message to the window; it waits when the mailbox is full, so a display that falls behind
/// holds frames back in QUIC's own receive queue, which drops its oldest. <i>The control loop</i> calls
/// <see cref="Dispose"/>, which posts a close message and waits for the display thread to finish.
/// </para>
/// <para>
/// Decoding on the window's thread is a choice: it avoids handing the decoder's texture between threads (it is
/// valid only until the next decode). The cost is that the window does not pump messages while a frame decodes.
/// </para>
/// </remarks>
internal sealed class BroadcastDisplay : IBroadcastDisplay
{
    private const uint FramesAvailable = 1;

    private readonly bool _windowed;
    private readonly Action<string> _log;
    private readonly ScreenLock? _screenLock;
    private readonly Thread _thread;
    private readonly TaskCompletionSource<bool> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly DisplayMailbox _mailbox;

    private FullscreenRenderWindow? _window;
    private ClientAgentSession? _session;
    private int _open;
    private int _disposed;
    private int _pictureHeld;

    private BroadcastDisplay(bool windowed, Action<string> log, ScreenLock? screenLock)
    {
        _windowed = windowed;
        _log = log;
        _screenLock = screenLock;
        _mailbox = new DisplayMailbox(LiveBounds.DisplayMailboxFrames, () => Volatile.Read(ref _window)?.PostAppMessage(FramesAvailable));
        _thread = new Thread(Run) { Name = "Promin display", IsBackground = true };
    }

    public bool IsOpen => Volatile.Read(ref _open) == 1;

    /// <summary>
    /// Makes the window, the device and the decoder on a new thread and returns once the window is up, or null
    /// when they could not be made (the reason is logged and nothing is left behind). With a
    /// <paramref name="screenLock"/>, keyboard and mouse are blocked while the picture is up.
    /// </summary>
    public static async Task<IBroadcastDisplay?> StartAsync(
        StartBroadcast message, bool windowed, Action<string> log, ScreenLock? screenLock)
    {
        var display = new BroadcastDisplay(windowed, log, screenLock);
        display._thread.Start();
        if (await display._ready.Task.ConfigureAwait(false))
        {
            log($"The broadcast is on screen ({message.Width}x{message.Height} at {message.TargetFps} fps).");
            return display;
        }

        display.Dispose();
        return null;
    }

    public ValueTask SubmitFrameAsync(byte[] streamPayload, CancellationToken ct) =>
        IsOpen ? _mailbox.SubmitAsync(streamPayload, ct) : ValueTask.CompletedTask;

    public ValueTask RestartAsync(CancellationToken ct) =>
        IsOpen ? _mailbox.RestartAsync(ct) : ValueTask.CompletedTask;

    /// <summary>
    /// Removes the broadcast from the screen and waits for the display thread to release everything. Input is
    /// released even when the thread does not finish in time.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Volatile.Write(ref _open, 0);
        _mailbox.Complete();
        Volatile.Read(ref _window)?.PostClose();

        if (!_thread.Join(TimeSpan.FromSeconds(10)))
        {
            _log("The display did not stop within 10 seconds. It is left to finish by itself.");
            ReleaseInput();
        }
    }

    // The root of the display thread. Whatever happens in the body or in the clean-up, an exception that left this
    // thread unhandled would end the whole agent; it is logged instead, and only this display is lost.
    private void Run()
    {
        try
        {
            RunDisplay();
        }
        catch (Exception failure)
        {
            _log($"The display thread stopped because of an unexpected error ({failure.GetType().Name}: {failure.Message}).");
            Volatile.Write(ref _open, 0);
            _ready.TrySetResult(false);
        }
    }

    private void RunDisplay()
    {
        GpuDevice? gpu = null;
        MediaFoundationH264Decoder? decoder = null;
        FullscreenRenderWindow? window = null;
        try
        {
            try
            {
                gpu = GpuDevice.Create();
                decoder = new MediaFoundationH264Decoder(gpu.Device);
                window = new FullscreenRenderWindow(gpu.Device, _windowed);
                window.AppMessage = OnAppMessage;
                window.Faulted = failure => Fail(window, failure);

                var session = new ClientAgentSession(new DecoderPresenter(decoder, window, _log));
                session.OnBroadcastStarted();
                var first = true;
                var capturedDecoder = decoder;
                session.FramePresented += frameId =>
                {
                    if (first)
                    {
                        first = false;
                        _log($"The first picture is on screen (frame {frameId}). Decoder: {capturedDecoder.DecoderName}, " +
                             $"on the GPU: {capturedDecoder.IsHardwareAccelerated}. {capturedDecoder.Configuration}");
                    }
                };

                Volatile.Write(ref _session, session);
                Volatile.Write(ref _window, window);
                Volatile.Write(ref _open, 1);
                _log($"Display ready on {gpu.AdapterName}.");
            }
            catch (Exception failure)
            {
                _log($"The broadcast cannot be shown on this PC ({failure.GetType().Name}: {failure.Message}).");
                _ready.TrySetResult(false);
                return;
            }

            _ready.TrySetResult(true);
            window.RunMessageLoop(_log, () => BlockInput(window));
        }
        finally
        {
            Volatile.Write(ref _open, 0);
            _mailbox.Complete();
            if (_session is { } session)
            {
                _log($"The display ended. Pictures shown: {session.PresentedFrameCount}, frames dropped: " +
                     $"{session.DroppedFrameCount}, discarded while waiting for a keyframe: {session.DiscardedFrameCount}, " +
                     $"refused by the decoder: {session.RefusedFrameCount}, decoded but not shown to stay current: " +
                     $"{session.SkippedPictureCount}.");
            }

            try
            {
                // The window first, so nothing can be drawn to it; then what it drew with. Each is released whether
                // or not the one before it threw (a removed device can make a dispose throw).
                Release("the window", () => window?.Dispose());
                Release("the decoder", () => decoder?.Dispose());
                Release("the graphics device", () => gpu?.Dispose());
            }
            finally
            {
                // Last, so input comes back only once the picture has gone.
                ReleaseInput();
            }
        }
    }

    // Runs on the display thread once the window is up. The picture is raised again after the lock yields to it:
    // a lock tick between the window showing and the yield would otherwise leave the lock window above the picture.
    private void BlockInput(FullscreenRenderWindow window)
    {
        if (_screenLock is null)
        {
            return;
        }

        Volatile.Write(ref _pictureHeld, 1);
        _screenLock.PictureShown();
        window.RaiseToTop();
    }

    // Called from the display thread and from Dispose; only the first call after BlockInput ends the block.
    private void ReleaseInput()
    {
        if (Interlocked.Exchange(ref _pictureHeld, 0) == 1)
        {
            _screenLock!.PictureGone();
        }
    }

    private void Release(string what, Action release)
    {
        try
        {
            release();
        }
        catch (Exception failure)
        {
            _log($"Releasing {what} failed ({failure.GetType().Name}: {failure.Message}).");
        }
    }

    private void OnAppMessage(uint number)
    {
        if (number != FramesAvailable)
        {
            return;
        }

        _mailbox.Drain(_session!);
    }

    // The display failed (the GPU went away, a decoder fault): this broadcast is no longer shown, and the agent goes on.
    private void Fail(FullscreenRenderWindow window, Exception failure)
    {
        _log($"The display failed and the broadcast is no longer shown ({failure.GetType().Name}: {failure.Message}).");
        Volatile.Write(ref _open, 0);
        window.Close();
    }
}
