using Promin.Protocol.Messages;
using Promin.Protocol.Sending;
using Promin.Protocol.Transport;

namespace Promin.Agent;

/// <summary>
/// One send the console PC opened on its connection, its first message already read. In the agent this is a QUIC
/// stream; the seam keeps <see cref="FileReceiver"/> testable with no network. Answered once, by
/// <see cref="ReceiveAsync"/> or <see cref="RefuseAsync"/>.
/// </summary>
internal interface IIncomingSend : IAsyncDisposable
{
    SendFiles Header { get; }

    /// <summary>
    /// Fills <paramref name="target"/>, answers the console PC, and returns the outcome it answered, as
    /// <see cref="SendExchange.ReceiveAsync"/> does; <paramref name="failed"/> gets the disk failure it answers as
    /// <see cref="SendOutcome.Failed"/>, and must not throw. Throws when the send ended early.
    /// </summary>
    Task<SendOutcome> ReceiveAsync(IReceiveTarget target, CancellationToken ct, Action<Exception>? failed = null);

    /// <summary>Answers <paramref name="outcome"/> without reading anything. Throws when the send has ended.</summary>
    Task RefuseAsync(SendOutcome outcome, CancellationToken ct);
}

/// <summary><see cref="IIncomingSend"/> over the send's own stream.</summary>
internal sealed class QuicIncomingSend(QuicControlChannel stream, SendFiles header) : IIncomingSend
{
    public SendFiles Header => header;

    public Task<SendOutcome> ReceiveAsync(IReceiveTarget target, CancellationToken ct, Action<Exception>? failed = null) =>
        SendExchange.ReceiveAsync(stream, header, target, ct, failed);

    public Task RefuseAsync(SendOutcome outcome, CancellationToken ct) => SendExchange.RefuseAsync(stream, outcome, ct);

    public ValueTask DisposeAsync() => stream.DisposeAsync();
}
