using Promin.Protocol.Messages;
using Promin.Protocol.Pairing;

namespace Promin.Protocol.Broadcast;

/// <summary>
/// The console PC's end of one agent PC's control stream, for as long as the connection lasts (across broadcasts).
/// It reads the stream all the time: the agent answers every <see cref="StartBroadcast"/> with
/// <see cref="BroadcastReady"/>, including a start re-sent after the display changed size that nobody waits for, and
/// an answer nobody waits for is dropped here instead of being taken, later, as the next start's answer.
/// </summary>
public sealed class ConsoleControl : IAsyncDisposable
{
    private readonly IControlChannel _channel;
    private readonly CancellationTokenSource _stop = new();
    private readonly object _gate = new();
    private readonly Task _reading;
    private TaskCompletionSource? _ready;
    private Exception? _ended;

    public ConsoleControl(IControlChannel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        _channel = channel;
        _reading = ReadAsync(_stop.Token);
    }

    public Task SendAsync(ControlMessage message, CancellationToken ct) => _channel.SendAsync(message, ct);

    /// <summary>
    /// Sends <paramref name="start"/> and completes when the agent PC answers that its display can take pictures.
    /// Throws <see cref="InvalidOperationException"/> when the stream has ended.
    /// </summary>
    public async Task StartAsync(StartBroadcast start, CancellationToken ct)
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            if (_ended is { } ended)
            {
                throw new InvalidOperationException("The control stream has ended.", ended);
            }

            _ready = ready;
        }

        await _channel.SendAsync(start, ct).ConfigureAwait(false);
        await ready.Task.WaitAsync(ct).ConfigureAwait(false);
    }

    private async Task ReadAsync(CancellationToken ct)
    {
        try
        {
            while (true)
            {
                // Nothing but BroadcastReady is sent to the console PC on this stream.
                if (await _channel.ReceiveAsync(ct).ConfigureAwait(false) is BroadcastReady)
                {
                    TaskCompletionSource? waiting;
                    lock (_gate)
                    {
                        (waiting, _ready) = (_ready, null);
                    }

                    waiting?.TrySetResult();
                }
            }
        }
        catch (Exception failure)
        {
            TaskCompletionSource? waiting;
            lock (_gate)
            {
                _ended = failure;
                (waiting, _ready) = (_ready, null);
            }

            waiting?.TrySetException(new InvalidOperationException("The control stream ended.", failure));
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        await _reading.ConfigureAwait(false);   // never throws: ReadAsync catches everything
        if (_channel is IAsyncDisposable disposable)
        {
            await disposable.DisposeAsync().ConfigureAwait(false);
        }

        _stop.Dispose();
    }
}
