using Promin.Protocol.Broadcast;
using Promin.Protocol.Transport;
using Promin.Protocol.Video;

namespace Promin.Protocol.Tests.Transport;

/// <summary>
/// How many whole frames a student PC keeps waiting for its display, and what the session makes of the frames it
/// loses when that bound is hit. The depth is something the agent passes when it creates its listener; every other
/// caller keeps the default.
/// </summary>
public class QuicReceiveQueueBoundTests
{
    private sealed class RecordingPresenter : IFramePresenter
    {
        public List<uint> Decoded { get; } = [];

        public bool TryDecode(uint frameId, bool isKeyframe, ReadOnlyMemory<byte> encodedFrame)
        {
            Decoded.Add(frameId);
            return true;
        }

        public void PresentDecoded()
        {
        }
    }

    private static byte[] Frame(uint frameId, bool isKeyframe)
    {
        var payload = new byte[VideoFrameEnvelope.Size + 100];
        new VideoFrameEnvelope(frameId, isKeyframe).WriteTo(payload);
        return payload;
    }

    private static async Task SettleAsync(QuicChannel agent, int expectedQueued, CancellationToken ct)
    {
        for (var i = 0; i < 400 && (agent.ActiveVideoDrains > 0 || agent.QueuedVideoFrames != expectedQueued); i++)
        {
            await Task.Delay(10, ct);
        }

        Assert.Equal(expectedQueued, agent.QueuedVideoFrames);
        Assert.Equal(0, agent.ActiveVideoDrains);
    }

    [Fact(Timeout = 30000)]
    public async Task TheDefaultDepth_IsEightFrames()
    {
        using var deadline = QuicLoopbackHarness.Deadline();
        await using var harness = await QuicLoopbackHarness.StartAsync(deadline.Token);
        var (master, agent) = await harness.ConnectAsync(deadline.Token);

        for (uint id = 0; id < 12; id++)
        {
            await master.SendVideoFrameAsync(Frame(id, id == 0), deadline.Token);
        }

        await SettleAsync(agent, expectedQueued: 8, deadline.Token);
        Assert.Equal(4u, VideoFrameEnvelope.ReadFrom(await agent.ReceiveVideoFrameAsync(deadline.Token)).FrameId);
    }

    /// <summary>
    /// With a depth of two and nobody reading, the oldest frames are lost in the receive queue and only the newest
    /// two remain. The session sees a gap before the first of them, discards until the next keyframe, keeps the
    /// picture it had, and is live again at that keyframe. The loss itself leaves no trace in the session's
    /// counters: only the discards that follow it.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task WhenTheBoundIsHit_TheOldestFramesAreLost_AndTheSessionTreatsItAsAGap()
    {
        using var deadline = QuicLoopbackHarness.Deadline();
        await using var harness = await QuicLoopbackHarness.StartAsync(
            deadline.Token, inboundFrameQueueCapacity: 2);
        var (master, agent) = await harness.ConnectAsync(deadline.Token);
        var presenter = new RecordingPresenter();
        var session = new ClientAgentSession(presenter);

        // The student PC is keeping up: frame 0 is read and decoded.
        await master.SendVideoFrameAsync(Frame(0, isKeyframe: true), deadline.Token);
        session.OnVideoFrameReceived(await agent.ReceiveVideoFrameAsync(deadline.Token));

        // Then it falls behind: frames 1 to 5 arrive while nothing is read.
        for (uint id = 1; id <= 5; id++)
        {
            await master.SendVideoFrameAsync(Frame(id, isKeyframe: false), deadline.Token);
        }

        await SettleAsync(agent, expectedQueued: 2, deadline.Token);

        // What is left is the newest two, 4 and 5.
        session.OnVideoFrameReceived(await agent.ReceiveVideoFrameAsync(deadline.Token));
        session.OnVideoFrameReceived(await agent.ReceiveVideoFrameAsync(deadline.Token));
        Assert.Equal([0u], presenter.Decoded);   // 4 and 5 follow a gap (1, 2, 3 are gone): nothing is decoded
        Assert.Equal(2, session.DiscardedFrameCount);
        Assert.Equal(0, session.DroppedFrameCount);
        Assert.Equal(0, session.RefusedFrameCount);

        // The next keyframe makes it live again.
        await master.SendVideoFrameAsync(Frame(6, isKeyframe: true), deadline.Token);
        session.OnVideoFrameReceived(await agent.ReceiveVideoFrameAsync(deadline.Token));
        Assert.Equal([0u, 6u], presenter.Decoded);
        Assert.Equal(2, session.PresentedFrameCount);
    }

    [Fact]
    public void ADepthBelowOne_IsRefused()
    {
        using var certificate = SelfSignedCertificateFactory.Create("agent-1");

        Assert.Throws<ArgumentOutOfRangeException>(() => new QuicAgentListener(
            certificate, new PeerCertificateStore(), port: 0, inboundFrameQueueCapacity: 0));
    }
}
