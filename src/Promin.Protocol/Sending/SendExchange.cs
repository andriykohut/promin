using System.Net.Quic;
using Promin.Protocol.Messages;
using Promin.Protocol.Transport;

namespace Promin.Protocol.Sending;

/// <summary>
/// Both ends of one file send. A send is its own bidirectional stream, opened by the console
/// on the standing connection: <see cref="SendFiles"/>, then a <see cref="SendItem"/> per item
/// with a file's raw bytes right behind it, then <see cref="SendEnd"/>. The agent answers once,
/// with <see cref="SendDone"/>.
/// </summary>
/// <remarks>
/// The agent may answer before the end. A refusal is a <see cref="SendDone"/> followed by
/// <see cref="QuicControlChannel.StopReceiving"/>, so the console's writes fail at once and it
/// reads the answer instead of writing bytes nobody will take. The console ends a send early by
/// aborting the stream; the agent then discards what it took.
/// </remarks>
public static class SendExchange
{
    /// <summary>The most file bytes one write carries; the console reports progress after each.</summary>
    private const int ChunkSize = 64 * 1024;

    /// <summary>
    /// Console end: sends <paramref name="items"/> and returns the agent's answer, which comes
    /// before every byte is written when the agent refuses. After each chunk of file bytes,
    /// <paramref name="written"/> gets the number of file bytes written so far.
    /// </summary>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="ct"/> was cancelled. The stream has been aborted.
    /// </exception>
    /// <exception cref="SendSourceException">
    /// An item could not be read, or does not hold the number of bytes it declared. The stream
    /// has been aborted.
    /// </exception>
    /// <remarks>Any other exception means the stream or the connection ended.</remarks>
    public static async Task<SendOutcome> SendAsync(
        QuicControlChannel channel,
        IReadOnlyList<OutgoingItem> items,
        bool open,
        Action<long> written,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(written);

        // Read from the start: a refusal can arrive while the bytes are still going out.
        var reply = ReceiveReplyAsync(channel, ct);
        try
        {
            try
            {
                await WriteAsync(channel, items, open, written, reply, ct).ConfigureAwait(false);
            }
            catch (QuicException e) when (e.ApplicationErrorCode == QuicControlChannel.RefusedErrorCode)
            {
                // The agent stopped receiving, which it does only after answering.
            }

            return await reply.ConfigureAwait(false);
        }
        catch
        {
            // Aborting fails the pending read too, so awaiting the reply here cannot hang, and its
            // own failure is observed rather than left on an abandoned task.
            channel.Abort(QuicControlChannel.RefusedErrorCode);
            try
            {
                await reply.ConfigureAwait(false);
            }
            catch
            {
                // The exception that ended the send is the one rethrown below.
            }

            throw;
        }
    }

    /// <summary>
    /// Agent end, after the stream's first message, <paramref name="header"/>, was read. Fills
    /// <paramref name="target"/>, answers the console, and returns the outcome it answered. A
    /// refusal, by <paramref name="target"/> or because the console broke the exchange (an item's
    /// bytes beyond the header's total, an unexpected message), discards, answers, and stops the
    /// console sending.
    /// </summary>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="ct"/> was cancelled. <paramref name="target"/> has been discarded.
    /// </exception>
    /// <remarks>
    /// Any other exception means the stream or the connection ended early, and
    /// <paramref name="target"/> has been discarded.
    /// </remarks>
    public static async Task<SendOutcome> ReceiveAsync(
        QuicControlChannel channel, SendFiles header, IReceiveTarget target, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(header);
        ArgumentNullException.ThrowIfNull(target);

        SendOutcome outcome;
        try
        {
            outcome = await TakeAsync(channel, header, target, ct).ConfigureAwait(false);
        }
        catch (SendRefusedException e)
        {
            return await DiscardAndRefuseAsync(channel, target, e.Outcome, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException
                                  && e is not (QuicException or ControlChannelClosedException))
        {
            // QuicException and ControlChannelClosedException are IOExceptions too, but they
            // mean the stream ended, not that the target failed.
            return await DiscardAndRefuseAsync(channel, target, SendOutcome.Failed, ct).ConfigureAwait(false);
        }
        catch
        {
            target.Discard();
            throw;
        }

        await channel.SendAsync(new SendDone(outcome), ct).ConfigureAwait(false);
        return outcome;
    }

    /// <summary>
    /// Agent end: answers <paramref name="outcome"/> without reading anything, for a send that
    /// cannot be taken at all (another is under way, nobody is signed in), and stops the console
    /// sending.
    /// </summary>
    public static async Task RefuseAsync(QuicControlChannel channel, SendOutcome outcome, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(channel);

        await channel.SendAsync(new SendDone(outcome), ct).ConfigureAwait(false);
        channel.StopReceiving();
    }

    private static async Task<SendOutcome> ReceiveReplyAsync(QuicControlChannel channel, CancellationToken ct) =>
        await channel.ReceiveAsync(ct).ConfigureAwait(false) is SendDone done
            ? done.Outcome
            : throw new IOException("The other PC answered a send with something other than SendDone.");

    /// <summary>Writes the whole send, or stops early once <paramref name="reply"/> has come.</summary>
    private static async Task WriteAsync(
        QuicControlChannel channel,
        IReadOnlyList<OutgoingItem> items,
        bool open,
        Action<long> written,
        Task reply,
        CancellationToken ct)
    {
        var totalBytes = items.Where(item => !item.IsFolder).Sum(item => item.Length);
        await channel.SendAsync(new SendFiles(totalBytes, open), ct).ConfigureAwait(false);

        var buffer = new byte[ChunkSize];
        long writtenSoFar = 0;
        foreach (var item in items)
        {
            if (reply.IsCompleted)
            {
                return;
            }

            await channel.SendAsync(new SendItem(item.Path, item.Length, item.IsFolder), ct).ConfigureAwait(false);
            if (item.IsFolder)
            {
                continue;
            }

            Stream source;
            try
            {
                source = item.Open!();
            }
            catch (Exception e)
            {
                throw new SendSourceException(item.Path, e);
            }

            await using (source.ConfigureAwait(false))
            {
                long sent = 0;
                int read;
                while ((read = await ReadSourceAsync(source, item, buffer, ct).ConfigureAwait(false)) > 0)
                {
                    sent += read;
                    if (sent > item.Length)
                    {
                        throw LengthChanged(item);
                    }

                    if (reply.IsCompleted)
                    {
                        return;
                    }

                    await channel.SendBytesAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    writtenSoFar += read;
                    written(writtenSoFar);
                }

                if (sent != item.Length)
                {
                    throw LengthChanged(item);
                }
            }
        }

        if (!reply.IsCompleted)
        {
            await channel.SendAsync(new SendEnd(), ct).ConfigureAwait(false);
        }
    }

    private static async Task<int> ReadSourceAsync(Stream source, OutgoingItem item, byte[] buffer, CancellationToken ct)
    {
        try
        {
            return await source.ReadAsync(buffer, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw new SendSourceException(item.Path, e);
        }
    }

    private static SendSourceException LengthChanged(OutgoingItem item) =>
        new(item.Path, new IOException($"It no longer holds the {item.Length} bytes it was picked with."));

    /// <summary>Takes items until <see cref="SendEnd"/>, and returns what <see cref="IReceiveTarget.Finish"/> says.</summary>
    private static async Task<SendOutcome> TakeAsync(
        QuicControlChannel channel, SendFiles header, IReceiveTarget target, CancellationToken ct)
    {
        target.Begin(header.TotalBytes);

        long received = 0;
        while (true)
        {
            switch (await channel.ReceiveAsync(ct).ConfigureAwait(false))
            {
                case SendItem { IsFolder: true } folder:
                    target.AddFolder(folder.Path);
                    break;

                // Bytes past the header's total would get round the target's free-space check.
                case SendItem file when file.Length >= 0 && file.Length <= header.TotalBytes - received:
                    received += file.Length;
                    var destination = target.AddFile(file.Path, file.Length);
                    await using (destination.ConfigureAwait(false))
                    {
                        await channel.ReceiveBytesAsync(destination, file.Length, ct).ConfigureAwait(false);
                    }

                    break;

                case SendEnd:
                    return target.Finish();

                default:
                    throw new SendRefusedException(SendOutcome.Failed);
            }
        }
    }

    private static async Task<SendOutcome> DiscardAndRefuseAsync(
        QuicControlChannel channel, IReceiveTarget target, SendOutcome outcome, CancellationToken ct)
    {
        target.Discard();
        await RefuseAsync(channel, outcome, ct).ConfigureAwait(false);
        return outcome;
    }
}
