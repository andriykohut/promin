namespace Promin.Protocol.Pairing;

/// <summary>A control channel that also carries raw bytes between whole messages, as a file send or an update does.</summary>
public interface IByteChannel : IControlChannel
{
    /// <summary>Writes raw bytes onto the stream, never inside a message's frame.</summary>
    Task SendBytesAsync(ReadOnlyMemory<byte> bytes, CancellationToken ct);

    /// <summary>Reads exactly <paramref name="count"/> raw bytes into <paramref name="destination"/>; throws when the stream ends first.</summary>
    Task ReceiveBytesAsync(Stream destination, long count, CancellationToken ct);
}
