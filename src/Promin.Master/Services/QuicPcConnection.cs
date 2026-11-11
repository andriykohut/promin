using Promin.Master.Core.Control;
using Promin.Master.Core.Wall;
using Promin.Protocol.Broadcast;
using Promin.Protocol.Messages;
using Promin.Protocol.Transport;

namespace Promin.Master.Services;

/// <summary>
/// One agent PC's standing connection: its screen pictures, the control stream a broadcast opens on first use and
/// keeps for the connection's life, and the lock stream opened the same way by the first lock message or report.
/// </summary>
public sealed class QuicPcConnection(QuicChannel channel) : IPcConnection
{
    private readonly AgentStreamDemux _streams = new(channel.ReceiveVideoFrameAsync);
    private readonly object _gate = new();
    private readonly CancellationTokenSource _closing = new();
    private Task<ConsoleControl>? _control;
    private Task<QuicControlChannel>? _lock;

    public QuicChannel Channel => channel;

    public ValueTask<byte[]> ReceivePictureAsync(CancellationToken ct) => _streams.ReceivePictureAsync(ct);

    /// <summary>The control stream, opened by the first broadcast on this connection. A failed open is tried again next time.</summary>
    public Task<ConsoleControl> ControlAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            if (_control is null || _control.IsFaulted || _control.IsCanceled)
            {
                _control = OpenAsync(ct);
            }

            return _control;
        }
    }

    /// <summary>The next <see cref="LockState"/> on the lock stream; anything else the agent sends there is skipped.</summary>
    public async Task<bool> ReceiveLockStateAsync(CancellationToken ct)
    {
        var stream = await LockStreamAsync().ConfigureAwait(false);
        while (true)
        {
            if (await stream.ReceiveAsync(ct).ConfigureAwait(false) is LockState state)
            {
                return state.Locked;
            }
        }
    }

    public async Task SendLockAsync(string message, CancellationToken ct) =>
        await (await LockStreamAsync().ConfigureAwait(false)).SendAsync(new LockPc(message), ct).ConfigureAwait(false);

    public async Task SendUnlockAsync(CancellationToken ct) =>
        await (await LockStreamAsync().ConfigureAwait(false)).SendAsync(new UnlockPc(), ct).ConfigureAwait(false);

    /// <summary>
    /// Opens a stream for this control session and waits for the agent's answer to <see cref="StartControl"/>. The
    /// agent's capture can take 5 seconds to start, so the wait is 10.
    /// </summary>
    public async Task<IControlSession> StartControlAsync(CancellationToken ct)
    {
        using var answering = CancellationTokenSource.CreateLinkedTokenSource(ct, _closing.Token);
        answering.CancelAfter(TimeSpan.FromSeconds(10));
        QuicControlChannel? stream = null;
        try
        {
            stream = await channel.OpenControlChannelAsync(answering.Token).ConfigureAwait(false);
            await stream.SendAsync(new StartControl(), answering.Token).ConfigureAwait(false);
            while (true)
            {
                if (await stream.ReceiveAsync(answering.Token).ConfigureAwait(false) is not ControlState state)
                {
                    continue;
                }

                if (state.Status == ControlStatus.Refused)
                {
                    throw new ControlRefusedException();
                }

                if (state.Status == ControlStatus.Started)
                {
                    return new QuicControlSession(stream, _streams.ReceiveControlFrameAsync);
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && !_closing.IsCancellationRequested)
        {
            await DisposeQuietlyAsync(stream).ConfigureAwait(false);
            throw new TimeoutException("The PC did not answer the request to control it.");
        }
        catch
        {
            await DisposeQuietlyAsync(stream).ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task<ConsoleControl>? control;
        Task<QuicControlChannel>? lockStream;
        lock (_gate)
        {
            control = _control;
            lockStream = _lock;
        }

        await _closing.CancelAsync().ConfigureAwait(false);

        // The streams close first so that a last message, such as an unlock, is still delivered: closing the connection
        // ends QUIC's retransmission.
        try
        {
            if (lockStream is { IsCompletedSuccessfully: true })
            {
                await lockStream.Result.DisposeAsync().ConfigureAwait(false);
            }

            if (control is { IsCompletedSuccessfully: true })
            {
                await control.Result.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            await channel.DisposeAsync().ConfigureAwait(false);
        }
    }

    // The agent tells the lock stream from the control stream by its first message. A failed open is tried again next time.
    private Task<QuicControlChannel> LockStreamAsync()
    {
        lock (_gate)
        {
            if (_lock is null || _lock.IsFaulted || _lock.IsCanceled)
            {
                _lock = OpenLockAsync(_closing.Token);
            }

            return _lock;
        }
    }

    private async Task<QuicControlChannel> OpenLockAsync(CancellationToken ct)
    {
        var stream = await channel.OpenControlChannelAsync(ct).ConfigureAwait(false);
        try
        {
            await stream.SendAsync(new WatchLock(), ct).ConfigureAwait(false);
        }
        catch
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        return stream;
    }

    private async Task<ConsoleControl> OpenAsync(CancellationToken ct) =>
        new(await channel.OpenControlChannelAsync(ct).ConfigureAwait(false));

    private static async Task DisposeQuietlyAsync(QuicControlChannel? stream)
    {
        if (stream is not null)
        {
            await stream.DisposeAsync().ConfigureAwait(false);
        }
    }
}
