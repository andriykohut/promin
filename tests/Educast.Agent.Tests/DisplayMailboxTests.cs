using Educast.Protocol.Broadcast;
using Educast.Protocol.Video;

namespace Educast.Agent.Tests;

/// <summary>
/// The hand-over between the receive loop and the display thread, with a real session and a presenter that records:
/// frames stay in order, a restart marker is ordered with the frames around it, and only the newest picture of a
/// waiting batch is shown.
/// </summary>
public class DisplayMailboxTests
{
    private sealed class RecordingPresenter : IFramePresenter
    {
        private uint _last;

        public List<uint> Decoded { get; } = [];
        public List<uint> Presented { get; } = [];

        public bool TryDecode(uint frameId, bool isKeyframe, ReadOnlyMemory<byte> encodedFrame)
        {
            _last = frameId;
            Decoded.Add(frameId);
            return true;
        }

        public void PresentDecoded() => Presented.Add(_last);
    }

    private static byte[] Frame(uint frameId, bool isKeyframe)
    {
        var payload = new byte[VideoFrameEnvelope.Size + 10];
        new VideoFrameEnvelope(frameId, isKeyframe).WriteTo(payload);
        return payload;
    }

    private static (DisplayMailbox Mailbox, ClientAgentSession Session, RecordingPresenter Presenter, Func<int> Wakes) Create(int capacity = 8)
    {
        var wakes = 0;
        var mailbox = new DisplayMailbox(capacity, () => Interlocked.Increment(ref wakes));
        var presenter = new RecordingPresenter();
        return (mailbox, new ClientAgentSession(presenter), presenter, () => Volatile.Read(ref wakes));
    }

    [Fact(Timeout = 10000)]
    public async Task ThreeFramesWaiting_AreDecodedInOrder_AndTheNewestIsShownOnce()
    {
        var (mailbox, session, presenter, _) = Create();
        await mailbox.SubmitAsync(Frame(0, true), default);
        await mailbox.SubmitAsync(Frame(1, false), default);
        await mailbox.SubmitAsync(Frame(2, false), default);

        mailbox.Drain(session);

        Assert.Equal([0u, 1u, 2u], presenter.Decoded);
        Assert.Equal([2u], presenter.Presented);
        Assert.Equal(2, session.SkippedPictureCount);
    }

    [Fact(Timeout = 10000)]
    public async Task AFrameThatArrivesAloneIsShown()
    {
        var (mailbox, session, presenter, _) = Create();
        await mailbox.SubmitAsync(Frame(0, true), default);
        mailbox.Drain(session);
        await mailbox.SubmitAsync(Frame(1, false), default);
        mailbox.Drain(session);

        Assert.Equal([0u, 1u], presenter.Presented);
        Assert.Equal(0, session.SkippedPictureCount);
    }

    [Fact(Timeout = 10000)]
    public async Task TheRestartMarker_ReachesTheSession_BetweenTheFramesAroundIt()
    {
        var (mailbox, session, presenter, _) = Create();
        await mailbox.SubmitAsync(Frame(500, true), default);
        await mailbox.SubmitAsync(Frame(501, false), default);
        await mailbox.RestartAsync(default);
        await mailbox.SubmitAsync(Frame(0, true), default);
        await mailbox.SubmitAsync(Frame(1, false), default);

        mailbox.Drain(session);

        // Without the marker reaching the session, frames 0 and 1 would be refused as older than 501.
        Assert.Equal([500u, 501u, 0u, 1u], presenter.Decoded);
        Assert.Equal([501u, 1u], presenter.Presented); // the old stream's newest picture, then the new stream's
        Assert.Equal(0, session.DroppedFrameCount);
    }

    [Fact(Timeout = 10000)]
    public async Task ARestartMarker_MakesTheSessionWaitForTheNewStreamsKeyframe()
    {
        var (mailbox, session, presenter, _) = Create();
        await mailbox.SubmitAsync(Frame(0, true), default);
        mailbox.Drain(session);

        await mailbox.RestartAsync(default);
        await mailbox.SubmitAsync(Frame(0, false), default);
        await mailbox.SubmitAsync(Frame(1, true), default);
        mailbox.Drain(session);

        Assert.Equal([0u, 1u], presenter.Decoded);
        Assert.Equal(1, session.DiscardedFrameCount);
    }

    [Fact(Timeout = 10000)]
    public async Task TheDisplayThreadIsWokenOnce_UntilItHasDrained()
    {
        var (mailbox, session, _, wakes) = Create();

        await mailbox.SubmitAsync(Frame(0, true), default);
        await mailbox.SubmitAsync(Frame(1, false), default);
        await mailbox.SubmitAsync(Frame(2, false), default);
        Assert.Equal(1, wakes());

        mailbox.Drain(session);
        await mailbox.SubmitAsync(Frame(3, false), default);

        Assert.Equal(2, wakes());
    }

    [Fact(Timeout = 10000)]
    public async Task WhenItIsFull_ASubmitWaits_UntilTheDisplayHasDrained()
    {
        var (mailbox, session, presenter, _) = Create(capacity: 2);
        await mailbox.SubmitAsync(Frame(0, true), default);
        await mailbox.SubmitAsync(Frame(1, false), default);

        var third = mailbox.SubmitAsync(Frame(2, false), default).AsTask();
        await Task.Delay(100);
        Assert.False(third.IsCompleted); // backpressure: the frame stays in the transport's queue

        mailbox.Drain(session);
        await third.WaitAsync(TimeSpan.FromSeconds(5));
        mailbox.Drain(session);

        Assert.Equal([0u, 1u, 2u], presenter.Decoded);
    }

    [Fact(Timeout = 10000)]
    public async Task OnceCompleted_SubmitsDoNothing_AndAWaitingSubmitIsReleased()
    {
        var (mailbox, session, presenter, wakes) = Create(capacity: 1);
        await mailbox.SubmitAsync(Frame(0, true), default);
        var waiting = mailbox.SubmitAsync(Frame(1, false), default).AsTask();
        await Task.Delay(50);

        mailbox.Complete();

        await waiting.WaitAsync(TimeSpan.FromSeconds(5));
        await mailbox.SubmitAsync(Frame(2, false), default);
        await mailbox.RestartAsync(default);
        mailbox.Drain(session);
        Assert.Equal([0u], presenter.Decoded); // what was queued before the end is still handed over
        Assert.Equal(1, wakes());
    }
}
