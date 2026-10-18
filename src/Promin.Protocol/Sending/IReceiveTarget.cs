using Promin.Protocol.Messages;

namespace Promin.Protocol.Sending;

/// <summary>
/// Where an agent puts what a send delivers. <see cref="SendExchange.ReceiveAsync"/> drives it:
/// <see cref="Begin"/>, then one call per item in arrival order, then <see cref="Finish"/>, or
/// <see cref="Discard"/> when the send ends any other way.
/// </summary>
/// <remarks>
/// Any method but <see cref="Discard"/> may throw <see cref="SendRefusedException"/> to refuse
/// the whole send with its outcome. An <see cref="IOException"/> or
/// <see cref="UnauthorizedAccessException"/> refuses it as <see cref="SendOutcome.Failed"/>.
/// </remarks>
public interface IReceiveTarget
{
    /// <summary>Starts a send that will carry <paramref name="totalBytes"/> file bytes in all.</summary>
    void Begin(long totalBytes);

    void AddFolder(string path);

    /// <summary>
    /// Returns where a file's bytes go. The exchange writes exactly <paramref name="length"/>
    /// bytes into it and then disposes it.
    /// </summary>
    Stream AddFile(string path, long length);

    /// <summary>
    /// Puts everything in place, and returns <see cref="SendOutcome.Sent"/>,
    /// <see cref="SendOutcome.InUse"/> or <see cref="SendOutcome.Failed"/>.
    /// </summary>
    SendOutcome Finish();

    /// <summary>Removes whatever this send left behind, so a send that ends early leaves nothing.</summary>
    void Discard();
}
