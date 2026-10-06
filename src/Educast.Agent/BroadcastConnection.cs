using Educast.Protocol.Messages;

namespace Educast.Agent;

/// <summary>
/// What the agent does with one teacher PC's connection. A display exists exactly while a broadcast does:
/// created when <see cref="StartBroadcast"/> arrives, removed on <see cref="StopBroadcast"/> and when the
/// connection ends. <b>Never</b> because no frame has come for a while: a still teacher screen sends none for as
/// long as it stays still, and a dead teacher PC shows up as the connection ending (QUIC's idle timeout).
/// </summary>
/// <remarks>
/// Two things run at once, both ending the other when they end: the control loop (messages) and the frame
/// pump (video streams). Frames that arrive while there is no display are read and dropped, so that frames of an
/// earlier broadcast never wait in the queue to be shown, ahead of the next one's, at its start.
/// </remarks>
internal sealed class BroadcastConnection(IBroadcastDisplayFactory displays, Action<string> log)
{
    private IBroadcastDisplay? _display;
    private int _framesDroppedWithNoDisplay;

    /// <summary>The display there is now, if any. For tests to know that a start has been handled.</summary>
    internal IBroadcastDisplay? Display => Volatile.Read(ref _display);

    /// <summary>
    /// Runs until the connection ends or <paramref name="ct"/> is cancelled, then removes the display. Throws
    /// nothing for what the connection or the teacher PC does.
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
                await HandleAsync(message, ending.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ending.IsCancellationRequested)
        {
            // Told to stop, or the video stream ended.
        }
        catch (Exception failure)
        {
            log($"The connection to the teacher PC ended: {Describe(failure)}");
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

    private async Task HandleAsync(ControlMessage message, CancellationToken ct)
    {
        switch (message)
        {
            case StartBroadcast { Width: > 0, Height: > 0, TargetFps: > 0 } start:
                await StartAsync(start, ct).ConfigureAwait(false);
                break;

            case StartBroadcast start:
                log($"Ignored a start of broadcast with an impossible size or rate ({start.Width}x{start.Height} at {start.TargetFps} fps).");
                break;

            case StopBroadcast:
                if (_display is not null)
                {
                    log("The teacher PC stopped the broadcast.");
                }

                RemoveDisplay();
                break;

            default:
                // Nothing else is sent to a student PC once it is paired.
                break;
        }
    }

    private async Task StartAsync(StartBroadcast start, CancellationToken ct)
    {
        if (_display is { IsOpen: true } open)
        {
            log($"The teacher PC started a new stream ({start.Width}x{start.Height} at {start.TargetFps} fps).");
            await open.RestartAsync(ct).ConfigureAwait(false);
            return;
        }

        // One that ended by itself is replaced.
        RemoveDisplay();
        log($"A broadcast started ({start.Width}x{start.Height} at {start.TargetFps} fps).");

        IBroadcastDisplay? display;
        try
        {
            display = await displays.StartAsync(start).ConfigureAwait(false);
        }
        catch (Exception failure)
        {
            log($"The broadcast could not be shown: {failure.Message}");
            return;
        }

        if (display is null)
        {
            log("The broadcast is not shown on this PC. It will be tried again if the teacher PC starts it again.");
            return;
        }

        Volatile.Write(ref _display, display);
    }

    private async Task PumpFramesAsync(IAgentConnection connection, CancellationTokenSource ending)
    {
        try
        {
            while (true)
            {
                var payload = await connection.ReceiveVideoFrameAsync(ending.Token).ConfigureAwait(false);
                if (Volatile.Read(ref _display) is { IsOpen: true } display)
                {
                    await display.SubmitFrameAsync(payload, ending.Token).ConfigureAwait(false);
                }
                else
                {
                    _framesDroppedWithNoDisplay++;
                }
            }
        }
        catch (OperationCanceledException) when (ending.IsCancellationRequested)
        {
        }
        catch (Exception failure)
        {
            log($"The video stream from the teacher PC ended: {Describe(failure)}");
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
