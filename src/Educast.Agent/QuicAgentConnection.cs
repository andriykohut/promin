using Educast.Protocol.Messages;
using Educast.Protocol.Transport;

namespace Educast.Agent;

/// <summary><see cref="IAgentConnection"/> over a pinned teacher PC's <see cref="QuicChannel"/>.</summary>
internal sealed class QuicAgentConnection(QuicChannel channel) : IAgentConnection, IAsyncDisposable
{
    // The control loop is the only caller of ReceiveControlAsync, one call at a time.
    private QuicControlChannel? _control;

    public async Task<ControlMessage> ReceiveControlAsync(CancellationToken ct)
    {
        // The teacher PC opens the control stream and speaks first; this side accepts it.
        _control ??= await channel.AcceptControlChannelAsync(ct).ConfigureAwait(false);
        return await _control.ReceiveAsync(ct).ConfigureAwait(false);
    }

    public ValueTask<byte[]> ReceiveVideoFrameAsync(CancellationToken ct) => channel.ReceiveVideoFrameAsync(ct);

    public async ValueTask DisposeAsync()
    {
        if (_control is not null)
        {
            await _control.DisposeAsync().ConfigureAwait(false);
        }

        await channel.DisposeAsync().ConfigureAwait(false);
    }
}
