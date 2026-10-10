using System.Threading.Channels;
using Promin.Protocol.Broadcast;

namespace Promin.Agent;

/// <summary>
/// How many frames may wait on an agent PC before it starts losing the oldest, so that a PC that cannot keep up
/// stays close to live instead of falling behind: at most <see cref="DisplayMailboxFrames"/> in the display's
/// mailbox plus <see cref="ReceiveQueueFrames"/> in the transport's receive queue, one being decoded, and the one a blocked
/// submitter is holding.
/// </summary>
internal static class LiveBounds
{
    public const int ReceiveQueueFrames = 2;
    public const int DisplayMailboxFrames = 2;
}

/// <summary>
/// The hand-over between the receive loop and the display thread: whole frames, in order, in a small bounded
/// queue, with a marker for "a new stream begins here" so that a restart is ordered with the frames around it.
/// </summary>
/// <remarks>
/// <para>
/// The receive loop (any thread) calls <see cref="SubmitAsync"/> and <see cref="RestartAsync"/>; each wakes the
/// display thread, once until it has drained. The display thread calls <see cref="Drain"/>. When the queue is full
/// a submit waits, so a display that is behind holds frames back in the transport's own receive queue, which
/// drops its oldest (see <c>QuicChannel</c>): that, and nothing here, is where frames are lost.
/// </para>
/// <para>
/// <see cref="Drain"/> gives the session everything that is waiting and tells it, for each frame, whether another
/// frame of the same stream is waiting behind it, so that the session shows only the newest picture of the batch.
/// </para>
/// </remarks>
internal sealed class DisplayMailbox
{
    // Frames, in order; null means "a new stream begins here".
    private readonly Channel<byte[]?> _queue;
    private readonly Action _wake;
    private int _wakePending;

    /// <param name="capacity">How many items may wait for the display thread.</param>
    /// <param name="wake">Tells the display thread to call <see cref="Drain"/>; may be called from any thread.</param>
    public DisplayMailbox(int capacity, Action wake)
    {
        _queue = Channel.CreateBounded<byte[]?>(
            new BoundedChannelOptions(capacity) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });
        _wake = wake;
    }

    /// <summary>Queues one frame's payload. Completes without effect once the mailbox has been completed.</summary>
    public ValueTask SubmitAsync(byte[] streamPayload, CancellationToken ct) => WriteAsync(streamPayload, ct);

    /// <summary>Queues a marker: what follows belongs to a new stream. Completes without effect once the mailbox has been completed.</summary>
    public ValueTask RestartAsync(CancellationToken ct) => WriteAsync(null, ct);

    /// <summary>Nothing more will be accepted; a waiting submit completes.</summary>
    public void Complete() => _queue.Writer.TryComplete();

    /// <summary>Hands everything that is waiting to <paramref name="session"/>. On the display thread only.</summary>
    public void Drain(ClientAgentSession session)
    {
        // Before reading, so that a submit that lands after the last read wakes the thread again.
        Volatile.Write(ref _wakePending, 0);
        while (_queue.Reader.TryRead(out var item))
        {
            if (item is null)
            {
                session.OnBroadcastStarted();
            }
            else
            {
                // A marker behind a frame ends its batch: the old stream's newest picture is shown before the restart.
                var moreWaiting = _queue.Reader.TryPeek(out var next) && next is not null;
                session.OnVideoFrameReceived(item, moreWaiting);
            }
        }
    }

    private async ValueTask WriteAsync(byte[]? item, CancellationToken ct)
    {
        try
        {
            await _queue.Writer.WriteAsync(item, ct).ConfigureAwait(false);
        }
        catch (ChannelClosedException)
        {
            return;
        }

        // One wake-up in flight at a time is enough: the drain takes everything that is there.
        if (Interlocked.Exchange(ref _wakePending, 1) == 0)
        {
            _wake();
        }
    }
}
