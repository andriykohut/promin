using Educast.Protocol.Messages;
using Educast.Protocol.Transport;
using Educast.Protocol.Video;

namespace Educast.Protocol.Tests.Transport;

/// <summary>
/// One unidirectional QUIC stream per encoded frame. QuicChannel never parses the envelope;
/// these tests assert the bytes handed in come back out whole, one frame per stream.
/// </summary>
public class QuicVideoStreamTests
{
    private static byte[] Frame(uint frameId, bool isKeyframe, int payloadBytes)
    {
        var envelope = new VideoFrameEnvelope(frameId, isKeyframe);
        var buffer = new byte[VideoFrameEnvelope.Size + payloadBytes];
        envelope.WriteTo(buffer);
        QuicLoopbackHarness.RandomBytes(payloadBytes).CopyTo(buffer.AsSpan(VideoFrameEnvelope.Size));
        return buffer;
    }

    [Fact(Timeout = 30000)]
    public async Task VideoFrame_RoundTripsWhole_OnItsOwnStream()
    {
        using var deadline = QuicLoopbackHarness.Deadline();
        await using var harness = await QuicLoopbackHarness.StartAsync(deadline.Token);
        var (master, agent) = await harness.ConnectAsync(deadline.Token);

        var sent = Frame(frameId: 42, isKeyframe: true, payloadBytes: 1024);
        await master.SendVideoFrameAsync(sent, deadline.Token);

        var received = await agent.ReceiveVideoFrameAsync(deadline.Token);

        Assert.Equal(sent, received);
        var envelope = VideoFrameEnvelope.ReadFrom(received);
        Assert.Equal(42u, envelope.FrameId);
        Assert.True(envelope.IsKeyframe);
    }

    /// <summary>
    /// A frame far larger than one QUIC packet still arrives as one buffer. Fails an
    /// implementation that reads the stream once and returns whatever happened to be there.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task LargeFrame_ArrivesIntact_AcrossManyPackets()
    {
        using var deadline = QuicLoopbackHarness.Deadline(30);
        await using var harness = await QuicLoopbackHarness.StartAsync(deadline.Token);
        var (master, agent) = await harness.ConnectAsync(deadline.Token);

        var sent = Frame(frameId: 1, isKeyframe: true, payloadBytes: 512 * 1024);
        await master.SendVideoFrameAsync(sent, deadline.Token);

        var received = await agent.ReceiveVideoFrameAsync(deadline.Token);

        Assert.Equal(sent.Length, received.Length);
        Assert.True(sent.AsSpan().SequenceEqual(received), "the frame came back corrupted");
    }

    /// <summary>
    /// Several frames in flight at once, each on its own stream. Fails an implementation that
    /// serialises frames onto one stream, or whose drain pump loses frames it accepted.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task ManyFrames_AllArrive_EachOnItsOwnStream()
    {
        using var deadline = QuicLoopbackHarness.Deadline(30);
        await using var harness = await QuicLoopbackHarness.StartAsync(deadline.Token);
        var (master, agent) = await harness.ConnectAsync(deadline.Token);

        const int frameCount = 6;
        var sent = new Dictionary<uint, byte[]>();
        for (uint id = 0; id < frameCount; id++)
        {
            sent[id] = Frame(id, isKeyframe: id == 0, payloadBytes: 8 * 1024);
        }

        // Receive concurrently: the inbound queue is small and deliberately drops the oldest
        // frame when the consumer is not keeping up.
        var receiveTask = Task.Run(async () =>
        {
            var received = new Dictionary<uint, byte[]>();
            while (received.Count < frameCount)
            {
                var frame = await agent.ReceiveVideoFrameAsync(deadline.Token);
                received[VideoFrameEnvelope.ReadFrom(frame).FrameId] = frame;
            }

            return received;
        });

        foreach (var frame in sent.Values)
        {
            await master.SendVideoFrameAsync(frame, deadline.Token);
        }

        var all = await receiveTask;
        Assert.Equal(frameCount, all.Count);
        foreach (var (id, frame) in sent)
        {
            Assert.True(frame.AsSpan().SequenceEqual(all[id]), $"frame {id} came back corrupted");
        }
    }

    /// <summary>
    /// Cancelling a send abandons that one frame — Task 9 drives this as per-client
    /// backpressure. Fails an implementation that ignores the token and delivers the frame
    /// anyway, and one whose abandoned stream wedges the connection for the frames after it.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task CancellingASend_AbandonsThatFrameOnly_AndLaterFramesStillArrive()
    {
        using var deadline = QuicLoopbackHarness.Deadline();
        await using var harness = await QuicLoopbackHarness.StartAsync(deadline.Token);
        var (master, agent) = await harness.ConnectAsync(deadline.Token);

        var first = Frame(frameId: 1, isKeyframe: true, payloadBytes: 2048);
        await master.SendVideoFrameAsync(first, deadline.Token);
        Assert.Equal(first, await agent.ReceiveVideoFrameAsync(deadline.Token));

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await master.SendVideoFrameAsync(Frame(2, false, 2048), cancelled.Token));

        var third = Frame(frameId: 3, isKeyframe: false, payloadBytes: 2048);
        await master.SendVideoFrameAsync(third, deadline.Token);

        var received = await agent.ReceiveVideoFrameAsync(deadline.Token);
        Assert.Equal(3u, VideoFrameEnvelope.ReadFrom(received).FrameId);
        Assert.Equal(third, received);
    }

    /// <summary>
    /// A frame beyond the ceiling is dropped rather than buffered without bound, and the
    /// connection survives it. Fails an implementation that reads an inbound stream to end with
    /// no size limit.
    /// </summary>
    [Fact(Timeout = 120000)]
    public async Task OversizedFrame_IsDropped_AndTheConnectionKeepsDeliveringFrames()
    {
        using var deadline = QuicLoopbackHarness.Deadline(60);
        await using var harness = await QuicLoopbackHarness.StartAsync(deadline.Token);
        var (master, agent) = await harness.ConnectAsync(deadline.Token);

        var oversized = new byte[QuicChannel.MaxVideoFrameBytes + 1024];
        new VideoFrameEnvelope(7, true).WriteTo(oversized);
        try
        {
            await master.SendVideoFrameAsync(oversized, deadline.Token);
        }
        catch (System.Net.Quic.QuicException)
        {
            // Expected: the receiver aborted the stream once the frame ran past the ceiling.
        }

        var good = Frame(frameId: 8, isKeyframe: false, payloadBytes: 4096);
        await master.SendVideoFrameAsync(good, deadline.Token);

        var received = await agent.ReceiveVideoFrameAsync(deadline.Token);
        Assert.Equal(8u, VideoFrameEnvelope.ReadFrom(received).FrameId);
    }

    /// <summary>
    /// Control and video share a connection but never share a stream. Fails an implementation
    /// whose accept pump dispatches on anything other than stream type.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task VideoAndControl_ShareOneConnection_WithoutMixing()
    {
        using var deadline = QuicLoopbackHarness.Deadline();
        await using var harness = await QuicLoopbackHarness.StartAsync(deadline.Token);
        var (master, agent) = await harness.ConnectAsync(deadline.Token);

        var (masterControl, agentPending) =
            await QuicLoopbackHarness.OpenControlPairAsync(master, agent, deadline.Token);
        await using var ownedMasterControl = masterControl;

        var frame = Frame(frameId: 99, isKeyframe: true, payloadBytes: 4096);
        await master.SendVideoFrameAsync(frame, deadline.Token);
        await masterControl.SendAsync(new StopBroadcast(), deadline.Token);
        await using var agentControl = await agentPending;

        Assert.Equal(frame, await agent.ReceiveVideoFrameAsync(deadline.Token));
        Assert.IsType<StopBroadcast>(await agentControl.ReceiveAsync(deadline.Token));
    }
}
