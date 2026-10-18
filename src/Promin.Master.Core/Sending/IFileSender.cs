using Promin.Master.Core.Rooms;
using Promin.Protocol.Messages;

namespace Promin.Master.Core.Sending;

/// <summary>Sends a <see cref="SendPackage"/> to one PC, as the console's file sending drives it.</summary>
public interface IFileSender
{
    /// <summary>
    /// Sends to one PC over its standing connection and returns the PC's answer; <paramref name="written"/> gets the
    /// file bytes written so far after each chunk. Null when it has none and none came up within 5 seconds.
    /// </summary>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled; the PC discards what it took.</exception>
    /// <exception cref="Promin.Protocol.Sending.SendSourceException">A file could not be read, or changed size while it was sent.</exception>
    /// <remarks>Any other exception means the connection ended.</remarks>
    Task<SendOutcome?> SendAsync(RoomMember pc, SendPackage package, bool open, Action<long> written, CancellationToken ct);
}
