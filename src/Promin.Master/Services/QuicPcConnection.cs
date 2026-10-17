using Promin.Master.Core.Wall;
using Promin.Protocol.Broadcast;
using Promin.Protocol.Transport;

namespace Promin.Master.Services;

/// <summary>
/// One agent PC's standing connection: its screen pictures, and the control stream a broadcast opens on first use and
/// keeps for the connection's life.
/// </summary>
public sealed class QuicPcConnection(QuicChannel channel) : IPcConnection
{
    private readonly object _gate = new();
    private Task<ConsoleControl>? _control;

    public QuicChannel Channel => channel;

    public ValueTask<byte[]> ReceivePictureAsync(CancellationToken ct) => channel.ReceiveVideoFrameAsync(ct);

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

    public async ValueTask DisposeAsync()
    {
        Task<ConsoleControl>? control;
        lock (_gate)
        {
            control = _control;
        }

        try
        {
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

    private async Task<ConsoleControl> OpenAsync(CancellationToken ct) =>
        new(await channel.OpenControlChannelAsync(ct).ConfigureAwait(false));
}
