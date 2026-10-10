using Promin.Protocol.Messages;
using Promin.Protocol.Transport;
using Promin.Protocol.Video;

namespace Promin.Protocol.Tests.Transport;

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
    /// Cancelling a send abandons that one frame — the broadcast session uses this as per-client
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

    /// <summary>
    /// The master receives no video, so it grants an agent no inbound unidirectional credit at
    /// all: a student-controlled machine cannot open a single stream at the teacher's laptop,
    /// let alone 256 of them at 4 MB each. The agent's attempt simply never gets a stream. Fails
    /// the symmetric 256-stream credit, under which this frame is delivered.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task Master_GrantsAnAgentNoInboundVideoStreams()
    {
        using var deadline = QuicLoopbackHarness.Deadline();
        await using var harness = await QuicLoopbackHarness.StartAsync(deadline.Token);
        var (master, agent) = await harness.ConnectAsync(deadline.Token);

        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        attempt.CancelAfter(TimeSpan.FromSeconds(2));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await agent.SendVideoFrameAsync(Frame(1, true, 1024), attempt.Token));

        using var brief = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        brief.CancelAfter(TimeSpan.FromMilliseconds(500));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await master.ReceiveVideoFrameAsync(brief.Token));
        Assert.Equal(0, master.ActiveVideoDrains);

        // The direction that matters is untouched.
        var frame = Frame(2, true, 1024);
        await master.SendVideoFrameAsync(frame, deadline.Token);
        Assert.Equal(frame, await agent.ReceiveVideoFrameAsync(deadline.Token));
    }

    /// <summary>
    /// However many streams a peer opens, only a bounded number are read into memory at once;
    /// the rest wait unread. Twenty streams are opened and deliberately left unfinished, so
    /// every drain that starts stays active. Fails a drain pump that spawns a reader per stream
    /// with no limit — there, all twenty are buffering at once.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task InboundVideoDrains_AreBoundedInConcurrency()
    {
        using var deadline = QuicLoopbackHarness.Deadline(30);
        await using var harness = await QuicLoopbackHarness.StartAsync(deadline.Token);

        var acceptTask = harness.Listener.AcceptAsync(deadline.Token);
        var rawMaster = await harness.ConnectRawAsync(deadline.Token);
        await using var agent = await acceptTask;

        const int opened = 20;
        var streams = new List<System.Net.Quic.QuicStream>();
        try
        {
            for (var i = 0; i < opened; i++)
            {
                var stream = await rawMaster.OpenOutboundStreamAsync(
                    System.Net.Quic.QuicStreamType.Unidirectional, deadline.Token);
                streams.Add(stream);

                // A first write is what makes the stream exist for the receiver; no FIN follows.
                await stream.WriteAsync(Frame((uint)i, false, 512), deadline.Token);
            }

            var observed = 0;
            for (var poll = 0; poll < 40; poll++)
            {
                await Task.Delay(50, deadline.Token);
                observed = Math.Max(observed, agent.ActiveVideoDrains);
            }

            Assert.Equal(QuicTransportSettings.MaxConcurrentInboundVideoStreams, observed);

            // Finishing the streams frees the slots, and the streams that were waiting are then
            // read too: the bound delays frames, it does not wedge the connection.
            foreach (var stream in streams)
            {
                stream.CompleteWrites();
            }

            for (var received = 0; received < QuicTransportSettings.MaxConcurrentInboundVideoStreams; received++)
            {
                await agent.ReceiveVideoFrameAsync(deadline.Token);
            }

            for (var poll = 0; poll < 100 && agent.ActiveVideoDrains != 0; poll++)
            {
                await Task.Delay(50, deadline.Token);
            }

            Assert.Equal(0, agent.ActiveVideoDrains);
        }
        finally
        {
            foreach (var stream in streams)
            {
                await stream.DisposeAsync();
            }
        }
    }
}
