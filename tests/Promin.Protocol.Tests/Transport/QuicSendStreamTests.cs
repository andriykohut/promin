using System.Net.Quic;
using Promin.Protocol.Messages;
using Promin.Protocol.Transport;

namespace Promin.Protocol.Tests.Transport;

/// <summary>
/// A send to one PC is its own bidirectional stream: control messages interleaved with raw
/// file bytes, over real QUIC.
/// </summary>
public class QuicSendStreamTests
{
    /// <summary>
    /// A message, then the bytes it announces, then the next message. The bytes land in the
    /// reader's buffer right behind the item, so the reader has to hand back what it had
    /// already buffered. Fails a receive that reads past the buffer or drops it.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task BytesFollowAMessage_AndAMessageFollowsTheBytes()
    {
        using var deadline = QuicLoopbackHarness.Deadline();
        await using var harness = await QuicLoopbackHarness.StartAsync(deadline.Token);
        var (master, agent) = await harness.ConnectAsync(deadline.Token);
        var (masterControl, agentPending) =
            await QuicLoopbackHarness.OpenControlPairAsync(master, agent, deadline.Token);
        await using var ownedMasterControl = masterControl;

        var sent = QuicLoopbackHarness.RandomBytes(1_048_576);
        var item = new SendItem("a.bin", sent.Length, false);
        // QUIC flow control holds the master's writes back until the agent reads, so the master
        // sends while the agent receives.
        var sending = Task.Run(async () =>
        {
            await masterControl.SendAsync(item, deadline.Token);
            await masterControl.SendBytesAsync(sent, deadline.Token);
            await masterControl.SendAsync(new SendEnd(), deadline.Token);
        });

        await using var agentControl = await agentPending;
        Assert.Equal(item, await agentControl.ReceiveAsync(deadline.Token));

        var received = new MemoryStream();
        await agentControl.ReceiveBytesAsync(received, sent.Length, deadline.Token);
        Assert.Equal(sent, received.ToArray());
        Assert.IsType<SendEnd>(await agentControl.ReceiveAsync(deadline.Token));
        await sending;
    }

    /// <summary>
    /// A peer that goes away before all the promised bytes arrive must produce an error, not
    /// an eternal wait. Fails a receive that treats the end of the stream as the end of the file.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task ReceiveBytes_WhenTheStreamEndsEarly_Throws()
    {
        using var deadline = QuicLoopbackHarness.Deadline();
        await using var harness = await QuicLoopbackHarness.StartAsync(deadline.Token);
        var (master, agent) = await harness.ConnectAsync(deadline.Token);
        var (masterControl, agentPending) =
            await QuicLoopbackHarness.OpenControlPairAsync(master, agent, deadline.Token);

        await masterControl.SendBytesAsync(QuicLoopbackHarness.RandomBytes(10), deadline.Token);
        await using var agentControl = await agentPending;
        await masterControl.DisposeAsync();

        await Assert.ThrowsAsync<ControlChannelClosedException>(
            () => agentControl.ReceiveBytesAsync(new MemoryStream(), 100, deadline.Token));
    }

    /// <summary>
    /// An agent that cannot take the files says so and then stops its peer sending. The
    /// master's writes fail at once, but the answer sent before that still reaches it. Fails a
    /// StopReceiving that tears down both directions and loses the reply.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task StopReceiving_FailsThePeersWrites_ButTheReplyStillArrives()
    {
        using var deadline = QuicLoopbackHarness.Deadline();
        await using var harness = await QuicLoopbackHarness.StartAsync(deadline.Token);
        var (master, agent) = await harness.ConnectAsync(deadline.Token);
        var (masterControl, agentPending) =
            await QuicLoopbackHarness.OpenControlPairAsync(master, agent, deadline.Token);
        await using var ownedMasterControl = masterControl;

        await masterControl.SendAsync(new SendFiles(1_000_000_000, false), deadline.Token);
        await using var agentControl = await agentPending;
        Assert.IsType<SendFiles>(await agentControl.ReceiveAsync(deadline.Token));

        await agentControl.SendAsync(new SendDone(SendOutcome.NotEnoughSpace), deadline.Token);
        agentControl.StopReceiving();

        using var writing = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        writing.CancelAfter(TimeSpan.FromSeconds(5));
        var chunk = QuicLoopbackHarness.RandomBytes(64 * 1024);
        await Assert.ThrowsAsync<QuicException>(async () =>
        {
            while (true)
            {
                await masterControl.SendBytesAsync(chunk, writing.Token);
            }
        });

        Assert.Equal(
            new SendDone(SendOutcome.NotEnoughSpace),
            await masterControl.ReceiveAsync(deadline.Token));
    }
}
