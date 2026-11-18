using System.Net.Quic;
using System.Threading.Channels;
using Promin.Protocol.Messages;
using Promin.Protocol.Pairing;

namespace Promin.Agent;

/// <summary>The parts of this PC that remote control uses, as functions, so that a test can swap each for a fake.</summary>
internal sealed record RemoteParts(
    Func<Func<byte[], CancellationToken, ValueTask>, Func<uint>, IControlVideo> OpenVideo,
    Func<IControlTab> OpenTab,
    Func<IClipboardWatch> OpenClipboard,
    IInputInjector Input,
    Func<bool> OnDesktop,
    TimeSpan DesktopPoll);

/// <summary>
/// The agent's side of the remote stream: a console PC that sends <see cref="StartViewing"/> sees this PC's screen as
/// video, with nothing on this PC to say so, until the stream or the connection ends or the video fails. After
/// <see cref="TakeControl"/> it also moves the pointer, presses keys and shares the clipboard, under the control tab;
/// a take-over that fails is refused and ends the stream. One stream at a time. Whatever way it ends, input is
/// released, the control tab goes and the previews come back, each step of that on its own so that one that fails does
/// not stop the rest. Without <c>parts</c> (a windowed agent) every stream is refused. The log gets a line for each
/// start, take-over and end, with the type of the failure, never a key or clipboard text.
/// </summary>
internal sealed class RemoteLink(RemoteParts? parts, ScreenLock? screenLock, IPreviewSwitch previews, bool mayControl, Action<string> log)
{
    // Frame ids count up across every control on this connection: the console PC's queue of control video lives as
    // long as the connection, so a frame left from an earlier control must never look newer than one of the next.
    private uint _nextFrameId;

    /// <summary>Serves remote streams until <paramref name="ct"/> is cancelled or the connection ends. Throws nothing.</summary>
    public async Task RunAsync(IAgentConnection connection, Func<byte[], CancellationToken, ValueTask> sendFrame, CancellationToken ct)
    {
        try
        {
            while (true)
            {
                var stream = await connection.AcceptRemoteStreamAsync(ct).ConfigureAwait(false);
                await ServeAsync(stream, sendFrame, ct).ConfigureAwait(false);
            }
        }
        catch (Exception e) when (e is OperationCanceledException or IOException or ChannelClosedException or QuicException or ObjectDisposedException)
        {
            // The connection ended, or ct was cancelled; the connection's handler logs which.
        }
        catch (Exception e)
        {
            log($"Remote control stopped after a failure: {e.GetType().Name}.");
        }
    }

    private async Task ServeAsync(IControlChannel stream, Func<byte[], CancellationToken, ValueTask> sendFrame, CancellationToken ct)
    {
        IControlVideo? video = null;
        IControlTab? tab = null;
        IClipboardWatch? clipboard = null;
        var controlled = false;
        var paused = false;
        var started = false;
        var reason = "the stream ended";

        // Run on TakeControl; null when a step failed. Whatever it took before failing, the finally below gives back.
        IClipboardWatch? TakeOver()
        {
            try
            {
                tab = parts!.OpenTab();
                controlled = tab.Show() && screenLock!.TryStartControl();
                clipboard = controlled ? parts.OpenClipboard() : null;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                log($"Remote control could not start: {e.GetType().Name}.");
            }

            return clipboard;
        }

        try
        {
            if (mayControl && screenLock is not null && parts is not null)
            {
                try
                {
                    // Set first: a pause that throws may have stopped the pictures all the same.
                    paused = true;
                    await previews.PauseAsync().ConfigureAwait(false);
                    video = parts.OpenVideo(sendFrame, () => Interlocked.Increment(ref _nextFrameId) - 1);
                    started = true;
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    log($"Remote viewing could not start: {e.GetType().Name}.");
                }
            }

            if (!started)
            {
                reason = "it was refused";
                await SendQuietlyAsync(stream, ControlStatus.Refused, ct).ConfigureAwait(false);
                return;
            }

            log("Remote viewing started.");
            reason = await ControlAsync(stream, video!, TakeOver, ct).ConfigureAwait(false);
        }
        finally
        {
            if (controlled)
            {
                Step(parts!.Input.ReleaseAll);
            }

            Step(() => clipboard?.Dispose());
            if (controlled)
            {
                Step(screenLock!.ControlEnded);
            }

            Step(() => tab?.Hide());
            Step(() => video?.Dispose());
            if (paused)
            {
                Step(previews.Resume);
            }

            if (started)
            {
                log($"Remote {(controlled ? "control" : "viewing")} ended: {reason}.");
            }

            if (stream is IAsyncDisposable closing)
            {
                try
                {
                    await closing.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    log($"Closing the remote stream failed: {e.GetType().Name}.");
                }
            }
        }
    }

    // A step that throws is logged by type only, and the next one still runs: input must be given back whatever fails.
    private void Step(Action step)
    {
        try
        {
            step();
        }
        catch (Exception e)
        {
            log($"A step of ending remote control failed: {e.GetType().Name}.");
        }
    }

    // Every send on the stream goes through one gate: the desktop poll and the clipboard report run on different tasks,
    // and a stream does not take two writers at once.
    private async Task<string> ControlAsync(IControlChannel stream, IControlVideo video, Func<IClipboardWatch?> takeOver, CancellationToken ct)
    {
        using var ending = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var gate = new SemaphoreSlim(1, 1);
        async Task SendAsync(ControlMessage message)
        {
            await gate.WaitAsync(ending.Token).ConfigureAwait(false);
            try
            {
                await stream.SendAsync(message, ending.Token).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        }

        // The handler runs on the clipboard's thread, where a throw would end the agent, so it only queues.
        var copied = Channel.CreateUnbounded<string>();
        void OnCopied(string text) => copied.Writer.TryWrite(text);

        // Set once control is taken. Control is taken on the receive loop, so no input after a TakeControl is read before it.
        IClipboardWatch? clipboard = null;
        var refused = false;
        async Task ReceiveAsync()
        {
            while (true)
            {
                var message = await stream.ReceiveAsync(ending.Token).ConfigureAwait(false);
                if (message is RequestKeyframe)
                {
                    video.RequestKeyframe();
                }
                else if (clipboard is not null)
                {
                    Apply(message, clipboard);
                }
                else if (message is TakeControl)
                {
                    if (takeOver() is not { } taken)
                    {
                        refused = true;
                        await SendAsync(new ControlState(ControlStatus.Refused)).ConfigureAwait(false);
                        return;
                    }

                    clipboard = taken;
                    clipboard.Copied += OnCopied;
                    log("Remote control started.");
                    await SendAsync(new ControlState(ControlStatus.Started)).ConfigureAwait(false);
                }
            }
        }

        var tasks = new Dictionary<Task, string>();
        try
        {
            // Started goes out before the video runs, and the video runs on a pool thread like the other loops, not on
            // this one: on a still screen its loop waits out each tick in the source and goes round without yielding.
            await SendAsync(new ControlState(ControlStatus.Started)).ConfigureAwait(false);
            tasks[Task.Run(() => video.RunAsync(ending.Token), CancellationToken.None)] = "the video stopped";
            tasks[Task.Run(ReceiveAsync, CancellationToken.None)] = "the stream ended";
            tasks[Task.Run(() => ReportCopiesAsync(copied.Reader, SendAsync, ending.Token), CancellationToken.None)] = "the clipboard report stopped";
            tasks[Task.Run(() => ReportDesktopAsync(SendAsync, ending.Token), CancellationToken.None)] = "the desktop report stopped";
            var first = await Task.WhenAny(tasks.Keys).ConfigureAwait(false);
            if (refused)
            {
                return "control was refused";
            }

            var failure = first.Exception?.GetBaseException();
            return failure is null || failure is OperationCanceledException
                ? tasks[first]
                : $"{tasks[first]} ({failure.GetType().Name})";
        }
        catch (Exception e)
        {
            return $"the stream ended ({e.GetType().Name})";
        }
        finally
        {
            // Drained before the clipboard is read: a take-over still running on the receive loop may yet set it.
            await ending.CancelAsync().ConfigureAwait(false);
            await Task.WhenAll(tasks.Keys).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            if (clipboard is not null)
            {
                clipboard.Copied -= OnCopied;
            }
        }
    }

    private void Apply(ControlMessage message, IClipboardWatch clipboard)
    {
        switch (message)
        {
            case PointerMove m: parts!.Input.Move(m.X, m.Y); break;
            case PointerButton b: parts!.Input.Button(b.Button, b.Down); break;
            case PointerWheel w: parts!.Input.Wheel(w.Delta, w.Horizontal); break;
            case KeyInput k: parts!.Input.Key(k.VirtualKey, k.ScanCode, k.Extended, k.Down); break;
            case ClipboardText { Text.Length: <= ControlLimits.MaxClipboardChars } t: clipboard.Write(t.Text); break;
        }
    }

    private static async Task ReportCopiesAsync(ChannelReader<string> copied, Func<ControlMessage, Task> send, CancellationToken ct)
    {
        while (true)
        {
            await send(new ClipboardText(await copied.ReadAsync(ct).ConfigureAwait(false))).ConfigureAwait(false);
        }
    }

    private async Task ReportDesktopAsync(Func<ControlMessage, Task> send, CancellationToken ct)
    {
        var onDesktop = true;
        while (true)
        {
            await Task.Delay(parts!.DesktopPoll, ct).ConfigureAwait(false);
            if (parts!.OnDesktop() != onDesktop)
            {
                onDesktop = !onDesktop;
                await send(new ControlState(onDesktop ? ControlStatus.Desktop : ControlStatus.ProtectedScreen)).ConfigureAwait(false);
            }
        }
    }

    private static async Task SendQuietlyAsync(IControlChannel stream, ControlStatus status, CancellationToken ct)
    {
        try
        {
            await stream.SendAsync(new ControlState(status), ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is OperationCanceledException or IOException or QuicException or ObjectDisposedException)
        {
            // The stream has gone: there is nobody to tell.
        }
    }
}
