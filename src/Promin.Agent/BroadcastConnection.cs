using Promin.Protocol.Messages;

namespace Promin.Agent;

/// <summary>
/// What the agent does with one console PC's connection. A display exists exactly while a broadcast does:
/// created when <see cref="StartBroadcast"/> arrives, removed on <see cref="StopBroadcast"/> and when the
/// connection ends. <b>Never</b> because no frame has come for a while: a still presenter's screen sends none for as
/// long as it stays still, and a dead console PC shows up as the connection ending (QUIC's idle timeout).
/// A display that ends by itself (it failed, or its window was closed) ends the connection, so the console PC
/// sees the PC as lost rather than receiving.
/// </summary>
/// <remarks>
/// Two things run at once, both ending the other when they end: the control loop (messages) and the frame
/// pump (video streams). Frames that arrive while there is no display are read and dropped, so that frames of an
/// earlier broadcast never wait in the queue to be shown, ahead of the next one's, at its start.
/// </remarks>
internal sealed class BroadcastConnection(
    IBroadcastDisplayFactory displays, Action<string> log, TimeSpan? startTimeout = null)
{
    /// <summary>How long a display may take to get its window up before the broadcast is given up on.</summary>
    public static readonly TimeSpan DefaultStartTimeout = TimeSpan.FromSeconds(20);

    private readonly TimeSpan _startTimeout = startTimeout ?? DefaultStartTimeout;

    private IBroadcastDisplay? _display;
    private int _framesDroppedWithNoDisplay;

    /// <summary>The display there is now, if any. For tests to know that a start has been handled.</summary>
    internal IBroadcastDisplay? Display => Volatile.Read(ref _display);

    /// <summary>
    /// Runs until the connection ends or <paramref name="ct"/> is cancelled, then removes the display. Throws
    /// nothing for what the connection or the console PC does.
    /// </summary>
    public async Task RunAsync(IAgentConnection connection, CancellationToken ct)
    {
        using var ending = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var pump = PumpFramesAsync(connection, ending);
        try
        {
            while (true)
            {
                var message = await connection.ReceiveControlAsync(ending.Token).ConfigureAwait(false);
                await HandleAsync(connection, message, ending.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ending.IsCancellationRequested)
        {
            // Told to stop, or the video stream ended.
        }
        catch (Exception failure)
        {
            log($"The connection to the console PC ended: {Describe(failure)}");
        }
        finally
        {
            await ending.CancelAsync().ConfigureAwait(false);
            await pump.ConfigureAwait(false);
            RemoveDisplay();
            if (_framesDroppedWithNoDisplay > 0)
            {
                log($"{_framesDroppedWithNoDisplay} video frames arrived with no broadcast on screen and were dropped.");
            }
        }
    }

    private async Task HandleAsync(IAgentConnection connection, ControlMessage message, CancellationToken ct)
    {
        switch (message)
        {
            case StartBroadcast { Width: > 0, Height: > 0, TargetFps: > 0 } start:
                if (await StartAsync(start, ct).ConfigureAwait(false))
                {
                    // The console PC holds its first keyframe until the display can take it.
                    await connection.SendControlAsync(new BroadcastReady(), ct).ConfigureAwait(false);
                }

                break;

            case StartBroadcast start:
                log($"Ignored a start of broadcast with an impossible size or rate ({start.Width}x{start.Height} at {start.TargetFps} fps).");
                break;

            case StopBroadcast:
                if (_display is not null)
                {
                    log("The console PC stopped the broadcast.");
                }

                RemoveDisplay();
                break;

            default:
                // Nothing else is sent to an agent PC once it is paired.
                break;
        }
    }

    /// <summary>True when a display is ready to take the stream's pictures; false when none could be made.</summary>
    private async Task<bool> StartAsync(StartBroadcast start, CancellationToken ct)
    {
        if (_display is { IsOpen: true } open)
        {
            log($"The console PC started a new stream ({start.Width}x{start.Height} at {start.TargetFps} fps).");
            await open.RestartAsync(ct).ConfigureAwait(false);
            return true;
        }

        // One that ended by itself is replaced.
        RemoveDisplay();
        log($"A broadcast started ({start.Width}x{start.Height} at {start.TargetFps} fps).");

        // A graphics driver that hangs while the device is made must not hold up this loop, and through it the
        // accept loop. The wait is bounded; a display that turns up after it was given up on is removed.
        Task<IBroadcastDisplay?>? starting = null;
        IBroadcastDisplay? display;
        try
        {
            starting = displays.StartAsync(start);
            display = await starting.WaitAsync(_startTimeout, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            log($"The broadcast could not be shown: the display was not ready after {_startTimeout.TotalSeconds.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)} seconds.");
            DisposeWhenReady(starting);
            return false;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            DisposeWhenReady(starting);
            throw;
        }
        catch (Exception failure)
        {
            log($"The broadcast could not be shown: {failure.Message}");
            return false;
        }

        if (display is null)
        {
            log("The broadcast is not shown on this PC. It will be tried again if the console PC starts it again.");
            return false;
        }

        Volatile.Write(ref _display, display);
        return true;
    }

    // A display that was given up on and then turned up anyway is not wanted. Null when the start threw before
    // there was anything to wait for.
    private static void DisposeWhenReady(Task<IBroadcastDisplay?>? starting) =>
        _ = starting?.ContinueWith(
            late =>
            {
                if (late.IsCompletedSuccessfully)
                {
                    late.Result?.Dispose();
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    private async Task PumpFramesAsync(IAgentConnection connection, CancellationTokenSource ending)
    {
        try
        {
            while (true)
            {
                var payload = await connection.ReceiveVideoFrameAsync(ending.Token).ConfigureAwait(false);
                var display = Volatile.Read(ref _display);
                if (display is null)
                {
                    _framesDroppedWithNoDisplay++;
                }
                else if (display.IsOpen)
                {
                    await display.SubmitFrameAsync(payload, ending.Token).ConfigureAwait(false);
                }
                else
                {
                    // Only a display that ended on its own is still here while closed: StopBroadcast, a newer
                    // connection and a lost connection all remove it. Ending the connection is how the presenter
                    // PC gets to know; its wall then reads Reconnecting instead of Receiving.
                    log("The broadcast's display on this PC ended by itself; this connection is closed so the console PC can see it.");
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (ending.IsCancellationRequested)
        {
        }
        catch (Exception failure)
        {
            log($"The video stream from the console PC ended: {Describe(failure)}");
        }
        finally
        {
            // Without video there is no broadcast; the control loop ends too.
            await ending.CancelAsync().ConfigureAwait(false);
        }
    }

    // A closed channel says only that it is closed; what it was closed by is more use in the log.
    private static string Describe(Exception failure) =>
        failure is System.Threading.Channels.ChannelClosedException { InnerException: { } cause } ? cause.Message : failure.Message;

    private void RemoveDisplay()
    {
        if (Interlocked.Exchange(ref _display, null) is { } display)
        {
            display.Dispose();
        }
    }
}
