using System.Buffers.Binary;
using System.Net.Quic;
using System.Text;
using Educast.Protocol.Messages;
using Educast.Protocol.Transport;

namespace Educast.Protocol.Tests.Transport;

/// <summary>
/// The reliable control stream over real QUIC, including what happens when the bytes arriving
/// on it are split across packets or are outright garbage.
/// </summary>
public class QuicControlStreamTests
{
    private static readonly byte[] PresharedKey = [7, 7, 7, 7];

    [Fact(Timeout = 30000)]
    public async Task ControlMessages_RoundTrip_InBothDirections()
    {
        using var deadline = QuicLoopbackHarness.Deadline();
        await using var harness = await QuicLoopbackHarness.StartAsync(deadline.Token);
        var (master, agent) = await harness.ConnectAsync(deadline.Token);

        var (masterControl, agentPending) =
            await QuicLoopbackHarness.OpenControlPairAsync(master, agent, deadline.Token);
        await using var ownedMasterControl = masterControl;

        await masterControl.SendAsync(new StartBroadcast(1920, 1080, 15), deadline.Token);
        await using var agentControl = await agentPending;
        var received = await agentControl.ReceiveAsync(deadline.Token);
        Assert.Equal(new StartBroadcast(1920, 1080, 15), received);

        await agentControl.SendAsync(new BroadcastReady(), deadline.Token);
        Assert.IsType<BroadcastReady>(await masterControl.ReceiveAsync(deadline.Token));

        // Two messages back to back must both survive: the second one lands in the same read
        // buffer as the first, so the reader has to consume exactly one frame at a time.
        await masterControl.SendAsync(new StartBroadcast(1280, 720, 10), deadline.Token);
        await masterControl.SendAsync(new StopBroadcast(), deadline.Token);
        Assert.Equal(new StartBroadcast(1280, 720, 10), await agentControl.ReceiveAsync(deadline.Token));
        Assert.IsType<StopBroadcast>(await agentControl.ReceiveAsync(deadline.Token));
    }

    /// <summary>
    /// A frame split across writes must be buffered and retried, which is what TryDecode
    /// returning false means. Fails an implementation that marks the partial buffer consumed
    /// (losing the prefix), or that treats "incomplete" as an error.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task ControlChannel_ReassemblesAFrameSplitAcrossSeparateWrites()
    {
        using var deadline = QuicLoopbackHarness.Deadline();
        await using var harness = await QuicLoopbackHarness.StartAsync(deadline.Token);

        var acceptTask = harness.Listener.AcceptAsync(deadline.Token);
        var rawMaster = await harness.ConnectRawAsync(deadline.Token);
        await using var agent = await acceptTask;

        var encoded = ControlMessageCodec.Encode(new StartBroadcast(1920, 1080, 15));
        await using var rawStream = await rawMaster.OpenOutboundStreamAsync(
            QuicStreamType.Bidirectional, deadline.Token);

        // Two bytes of the length prefix, then a pause long enough for the receiver to observe
        // an incomplete buffer, then the rest.
        await rawStream.WriteAsync(encoded.AsMemory(0, 2), deadline.Token);
        await using var agentControl = await agent.AcceptControlChannelAsync(deadline.Token);
        var receiveTask = agentControl.ReceiveAsync(deadline.Token);

        await Task.Delay(250, deadline.Token);
        Assert.False(receiveTask.IsCompleted, "the reader returned a message before the frame was complete");

        await rawStream.WriteAsync(encoded.AsMemory(2, 3), deadline.Token);
        await Task.Delay(250, deadline.Token);
        Assert.False(receiveTask.IsCompleted, "the reader returned a message before the frame was complete");

        await rawStream.WriteAsync(encoded.AsMemory(5), deadline.Token);

        Assert.Equal(new StartBroadcast(1920, 1080, 15), await receiveTask);
    }

    /// <summary>
    /// Definitively corrupt framing must surface as ControlMessageFormatException and take down
    /// this connection only. Fails an implementation that lets the exception escape the accept
    /// loop and kill the listener, and one that silently swallows corruption and hangs.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task CorruptFrame_FaultsOnlyItsOwnConnection_AndTheListenerKeepsServing()
    {
        using var deadline = QuicLoopbackHarness.Deadline();
        await using var harness = await QuicLoopbackHarness.StartAsync(deadline.Token);

        var acceptTask = harness.Listener.AcceptAsync(deadline.Token);
        var rawMaster = await harness.ConnectRawAsync(deadline.Token);
        await using (var agent = await acceptTask)
        {
            var payload = Encoding.UTF8.GetBytes("this is not json at all");
            var framed = new byte[4 + payload.Length];
            BinaryPrimitives.WriteInt32BigEndian(framed, payload.Length);
            payload.CopyTo(framed.AsSpan(4));

            await using var rawStream = await rawMaster.OpenOutboundStreamAsync(
                QuicStreamType.Bidirectional, deadline.Token);
            await rawStream.WriteAsync(framed, deadline.Token);

            await using var agentControl = await agent.AcceptControlChannelAsync(deadline.Token);
            await Assert.ThrowsAsync<ControlMessageFormatException>(
                () => agentControl.ReceiveAsync(deadline.Token));
        }

        // The listener, and every other client's session, must be entirely unaffected.
        var (master, secondAgent) = await harness.ConnectAsync(deadline.Token);
        var (masterControl, agentPending) =
            await QuicLoopbackHarness.OpenControlPairAsync(master, secondAgent, deadline.Token);
        await using var ownedMasterControl = masterControl;

        var masterTask = QuicPairingHandshake.RunAsMasterAsync(masterControl, PresharedKey, deadline.Token);
        await using var secondAgentControl = await agentPending;
        var results = await Task.WhenAll(
            masterTask,
            QuicPairingHandshake.RunAsAgentAsync(secondAgentControl, PresharedKey, deadline.Token));
        Assert.All(results, succeeded => Assert.True(succeeded));
    }

    /// <summary>
    /// A length prefix larger than the maximum frame must be rejected on sight rather than
    /// buffered. Fails an implementation that accumulates bytes waiting for a frame that will
    /// never arrive — here, 2GB of them.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task ControlChannel_RejectsAnOversizedLengthPrefix_WithoutBuffering()
    {
        using var deadline = QuicLoopbackHarness.Deadline();
        await using var harness = await QuicLoopbackHarness.StartAsync(deadline.Token);

        var acceptTask = harness.Listener.AcceptAsync(deadline.Token);
        var rawMaster = await harness.ConnectRawAsync(deadline.Token);
        await using var agent = await acceptTask;

        var framed = new byte[8];
        BinaryPrimitives.WriteInt32BigEndian(framed, int.MaxValue);

        await using var rawStream = await rawMaster.OpenOutboundStreamAsync(
            QuicStreamType.Bidirectional, deadline.Token);
        await rawStream.WriteAsync(framed, deadline.Token);

        await using var agentControl = await agent.AcceptControlChannelAsync(deadline.Token);
        var failure = await Assert.ThrowsAsync<ControlMessageFormatException>(
            () => agentControl.ReceiveAsync(deadline.Token));
        Assert.Contains("exceeds maximum frame size", failure.Message);
    }

    /// <summary>
    /// A peer that closes the stream mid-frame must produce an error, not an eternal wait.
    /// Fails an implementation that loops forever once the reader reports completion.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task ReceiveAsync_Throws_WhenThePeerClosesTheStreamMidMessage()
    {
        using var deadline = QuicLoopbackHarness.Deadline();
        await using var harness = await QuicLoopbackHarness.StartAsync(deadline.Token);

        var acceptTask = harness.Listener.AcceptAsync(deadline.Token);
        var rawMaster = await harness.ConnectRawAsync(deadline.Token);
        await using var agent = await acceptTask;

        var encoded = ControlMessageCodec.Encode(new StartBroadcast(1920, 1080, 15));

        await using var rawStream = await rawMaster.OpenOutboundStreamAsync(
            QuicStreamType.Bidirectional, deadline.Token);
        await rawStream.WriteAsync(encoded.AsMemory(0, 6), completeWrites: true, deadline.Token);

        await using var agentControl = await agent.AcceptControlChannelAsync(deadline.Token);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => agentControl.ReceiveAsync(deadline.Token));
    }
}
