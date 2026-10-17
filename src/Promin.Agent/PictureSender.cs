using System.Net.Quic;
using Promin.MediaPipeline.Pictures;

namespace Promin.Agent;

/// <summary>
/// Sends this PC's screen to the console PC as small JPEG pictures for as long as its connection lasts: at most one
/// a second, only when the screen changed, and one at a time (the next is taken only once the last has arrived, so
/// a slow network means fewer pictures, never a queue). When the screen cannot be captured it sends one empty
/// picture, which the console shows as "screen not available"; the next picture clears it.
/// </summary>
/// <remarks>
/// Nothing it does ends the connection or touches the broadcast on screen. A capture that fails is closed and
/// opened again on the next turn; the reason is logged once until it changes.
/// </remarks>
internal sealed class PictureSender(
    Func<IScreenPictures> open,
    Func<byte[], CancellationToken, ValueTask> send,
    Action<string> log,
    TimeProvider time)
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);

    private IScreenPictures? _pictures;
    private bool _notAvailableSent;
    private string? _lastProblem;

    /// <summary>
    /// Runs until <paramref name="ct"/> is cancelled or a send fails. Never throws. A send that fails because the
    /// connection ended is not logged (the connection's handler logs the end); any other failure is.
    /// </summary>
    public async Task RunAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var started = time.GetTimestamp();
                await StepAsync(ct).ConfigureAwait(false);
                var rest = Interval - time.GetElapsedTime(started);
                if (rest > TimeSpan.Zero)
                {
                    await Task.Delay(rest, time, ct).ConfigureAwait(false);
                }
            }
        }
        catch (Exception failure) when (failure is not (OperationCanceledException or QuicException or ObjectDisposedException))
        {
            // The connection is fine but no more pictures will come: that must be in the log.
            log($"Screen pictures stopped: {failure.Message}");
        }
        catch (Exception)
        {
            // Cancelled, or a send failed because the connection ended: its handler logs the end.
        }
        finally
        {
            _pictures?.Dispose();
            _pictures = null;
        }
    }

    /// <summary>One turn: take the screen, send what there is to send. Throws only what the send throws.</summary>
    internal async Task StepAsync(CancellationToken ct)
    {
        switch (Take(out var jpeg))
        {
            case PictureOutcome.Picture:
                await send(jpeg, ct).ConfigureAwait(false);
                _notAvailableSent = false;
                break;

            case PictureOutcome.Unavailable when !_notAvailableSent:
                // An empty picture is how "screen not available" travels.
                await send([], ct).ConfigureAwait(false);
                _notAvailableSent = true;
                break;
        }
    }

    // Blocks for up to Interval while the screen is still: the capture waits for a change.
    private PictureOutcome Take(out byte[] jpeg)
    {
        jpeg = [];
        try
        {
            _pictures ??= open();
            var outcome = _pictures.TryTake(Interval, out jpeg);
            _lastProblem = null;
            return outcome;
        }
        catch (Exception failure)
        {
            _pictures?.Dispose();
            _pictures = null;
            if (failure.Message != _lastProblem)
            {
                log($"The screen could not be captured: {failure.Message}");
                _lastProblem = failure.Message;
            }

            return PictureOutcome.Unavailable;
        }
    }
}
