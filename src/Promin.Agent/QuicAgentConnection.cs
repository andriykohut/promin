using System.Threading.Channels;
using Promin.Protocol.Messages;
using Promin.Protocol.Pairing;
using Promin.Protocol.Transport;

namespace Promin.Agent;

/// <summary>
/// <see cref="IAgentConnection"/> over a pinned console PC's <see cref="QuicChannel"/>. The console PC opens every
/// bidirectional stream and speaks first on it, and its first message says what the stream is: a send begins with
/// <see cref="SendFiles"/>, the lock stream with <see cref="WatchLock"/>, and the first stream to begin with anything
/// else is the control stream.
/// </summary>
internal sealed class QuicAgentConnection(QuicChannel channel) : IAgentConnection, IAsyncDisposable
{
    /// <summary>How long a new stream may take to send its first message before it is aborted.</summary>
    private static readonly TimeSpan FirstMessageWait = TimeSpan.FromSeconds(10);

    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly TaskCompletionSource<ControlMessage> _firstControlMessage = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Channel<IIncomingSend> _sends = Channel.CreateUnbounded<IIncomingSend>();
    private readonly Channel<QuicControlChannel> _lockStreams = Channel.CreateBounded<QuicControlChannel>(1);
    private Task? _sorting;
    private QuicControlChannel? _control;
    private QuicControlChannel? _lockStream;

    // The control loop is the only caller of ReceiveControlAsync, one call at a time.
    private bool _firstControlMessageTaken;

    public async Task<ControlMessage> ReceiveControlAsync(CancellationToken ct)
    {
        StartSorting();
        if (_firstControlMessageTaken)
        {
            return await _control!.ReceiveAsync(ct).ConfigureAwait(false);
        }

        var first = await _firstControlMessage.Task.WaitAsync(ct).ConfigureAwait(false);
        _firstControlMessageTaken = true;
        return first;
    }

    public async Task<IIncomingSend> AcceptSendAsync(CancellationToken ct)
    {
        StartSorting();
        return await _sends.Reader.ReadAsync(ct).ConfigureAwait(false);
    }

    public async Task<IControlChannel> AcceptLockStreamAsync(CancellationToken ct)
    {
        StartSorting();
        return await _lockStreams.Reader.ReadAsync(ct).ConfigureAwait(false);
    }

    public ValueTask<byte[]> ReceiveVideoFrameAsync(CancellationToken ct) => channel.ReceiveVideoFrameAsync(ct);

    // Only ever called after a message was received, so the stream is there; sends and receives do not block each other.
    public Task SendControlAsync(ControlMessage message, CancellationToken ct) =>
        (_control ?? throw new InvalidOperationException("The console PC has not opened its control stream yet.")).SendAsync(message, ct);

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        if (_sorting is not null)
        {
            await _sorting.ConfigureAwait(false);
        }

        // The connection goes first, unlike on the console's end, which has last messages to deliver: a replacement
        // connection must not wait on a peer that has gone. A stream waits for the peer to acknowledge its close, about
        // 16 s when the peer was killed, and a stream whose connection has gone closes at once.
        await channel.DisposeAsync().ConfigureAwait(false);

        // Sends that arrived after the receiver stopped taking them.
        while (_sends.Reader.TryRead(out var send))
        {
            await send.DisposeAsync().ConfigureAwait(false);
        }

        if (_control is not null)
        {
            await _control.DisposeAsync().ConfigureAwait(false);
        }

        if (_lockStream is not null)
        {
            await _lockStream.DisposeAsync().ConfigureAwait(false);
        }

        _stop.Dispose();
    }

    private void StartSorting()
    {
        lock (_gate)
        {
            _sorting ??= Task.Run(() => SortStreamsAsync(_stop.Token), CancellationToken.None);
        }
    }

    /// <summary>
    /// Sorts the console PC's streams by their first message until the connection ends or this is disposed, then
    /// fails the wait for the control stream, the lock stream and sends with what ended it.
    /// </summary>
    private async Task SortStreamsAsync(CancellationToken ct)
    {
        try
        {
            while (true)
            {
                var stream = await channel.AcceptControlChannelAsync(ct).ConfigureAwait(false);
                switch (await FirstMessageAsync(stream, ct).ConfigureAwait(false))
                {
                    case SendFiles header:
                        _sends.Writer.TryWrite(new QuicIncomingSend(stream, header));
                        break;

                    case WatchLock when _lockStream is null:
                        _lockStream = stream;
                        _lockStreams.Writer.TryWrite(stream);
                        break;

                    case { } first when first is not WatchLock && _control is null:
                        _control = stream;
                        _firstControlMessage.TrySetResult(first);
                        break;

                    default:
                        // Silent, broken, or a second control or lock stream: a connection has one of each.
                        stream.Abort(QuicControlChannel.CorruptFrameErrorCode);
                        await stream.DisposeAsync().ConfigureAwait(false);
                        break;
                }
            }
        }
        catch (Exception ended)
        {
            _firstControlMessage.TrySetException(ended);

            // Marked observed: when nobody waits for the control stream any more, this is not a failure to log.
            _ = _firstControlMessage.Task.Exception;
            _sends.Writer.TryComplete(ended);
            _lockStreams.Writer.TryComplete(ended);
        }
    }

    /// <summary>The stream's first message, or null when none came within <see cref="FirstMessageWait"/> or the stream broke off.</summary>
    private static async Task<ControlMessage?> FirstMessageAsync(QuicControlChannel stream, CancellationToken ct)
    {
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
        wait.CancelAfter(FirstMessageWait);
        try
        {
            return await stream.ReceiveAsync(wait.Token).ConfigureAwait(false);
        }
        catch (Exception e) when (e is OperationCanceledException or IOException or ControlMessageFormatException)
        {
            return null;
        }
    }
}
