using System.Threading.Channels;
using Educast.Protocol.Messages;
using Educast.Protocol.Pairing;

namespace Educast.Protocol.Tests.TestDoubles;

public sealed class InMemoryControlChannel : IControlChannel
{
    private readonly Channel<ControlMessage> _inbound = Channel.CreateUnbounded<ControlMessage>();

    public static (InMemoryControlChannel a, InMemoryControlChannel b) CreatePair()
    {
        var a = new InMemoryControlChannel();
        var b = new InMemoryControlChannel();
        a.LinkTo(b);
        b.LinkTo(a);
        return (a, b);
    }

    private InMemoryControlChannel? _peer;
    private void LinkTo(InMemoryControlChannel peer) => _peer = peer;

    public Task SendAsync(ControlMessage message, CancellationToken ct) =>
        (_peer ?? throw new InvalidOperationException("Not linked")).Deliver(message, ct);

    private async Task Deliver(ControlMessage message, CancellationToken ct) =>
        await _inbound.Writer.WriteAsync(message, ct);

    public async Task<ControlMessage> ReceiveAsync(CancellationToken ct) =>
        await _inbound.Reader.ReadAsync(ct);
}
