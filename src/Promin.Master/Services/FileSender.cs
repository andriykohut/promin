using Promin.Master.Core.Rooms;
using Promin.Master.Core.Sending;
using Promin.Master.Core.Wall;
using Promin.Protocol.Messages;
using Promin.Protocol.Sending;

namespace Promin.Master.Services;

/// <summary>
/// <see cref="IFileSender"/> over the room connections: each send is a fresh stream on the PC's standing connection,
/// which a send never waits more than five seconds for and never closes. Composition only; the exchange is
/// <see cref="SendExchange"/>.
/// </summary>
public sealed class FileSender(RoomConnections<QuicPcConnection> connections) : IFileSender
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);

    public async Task<SendOutcome?> SendAsync(RoomMember pc, SendPackage package, bool open, Action<long> written, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(pc);
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(written);

        QuicPcConnection? connection;
        using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            deadline.CancelAfter(ConnectTimeout);
            try
            {
                connection = await connections.GetAsync(pc, deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // The five seconds ran out: the PC cannot be reached, which is not the operator cancelling.
                return null;
            }
        }

        if (connection is null)
        {
            return null;
        }

        await using var stream = await connection.Channel.OpenControlChannelAsync(ct).ConfigureAwait(false);
        return await SendExchange.SendAsync(stream, package.Items, open, written, ct).ConfigureAwait(false);
    }
}
