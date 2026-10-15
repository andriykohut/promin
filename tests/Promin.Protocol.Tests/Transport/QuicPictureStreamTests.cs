using Promin.Protocol.Messages;
using Promin.Protocol.Transport;

namespace Promin.Protocol.Tests.Transport;

/// <summary>Screen pictures travel agent to console, one stream each, on the connection the console dialled.</summary>
public class QuicPictureStreamTests
{
    [Fact(Timeout = 30000)]
    public async Task APicture_FromTheAgent_ArrivesWhole_OnTheConsolesConnection()
    {
        using var deadline = QuicLoopbackHarness.Deadline();
        await using var harness = await QuicLoopbackHarness.StartAsync(deadline.Token);
        var (master, agent) = await harness.ConnectAsync(deadline.Token);

        var picture = QuicLoopbackHarness.RandomBytes(40 * 1024);
        await agent.SendVideoFrameAsync(picture, deadline.Token);

        Assert.Equal(picture, await master.ReceiveVideoFrameAsync(deadline.Token));
    }

    /// <summary>"Screen not available" is an empty stream; it must arrive as an empty picture, not vanish.</summary>
    [Fact(Timeout = 30000)]
    public async Task AnEmptyStream_ArrivesAsAnEmptyPicture()
    {
        using var deadline = QuicLoopbackHarness.Deadline();
        await using var harness = await QuicLoopbackHarness.StartAsync(deadline.Token);
        var (master, agent) = await harness.ConnectAsync(deadline.Token);

        await agent.SendVideoFrameAsync(ReadOnlyMemory<byte>.Empty, deadline.Token);

        Assert.Empty(await master.ReceiveVideoFrameAsync(deadline.Token));
    }

    [Fact(Timeout = 30000)]
    public async Task APictureOverOneMegabyte_IsRefused_AndTheConnectionCarriesOn()
    {
        using var deadline = QuicLoopbackHarness.Deadline();
        await using var harness = await QuicLoopbackHarness.StartAsync(deadline.Token);
        var (master, agent) = await harness.ConnectAsync(deadline.Token);

        try
        {
            await agent.SendVideoFrameAsync(QuicLoopbackHarness.RandomBytes(QuicChannel.MaxPictureBytes + 1), deadline.Token);
        }
        catch (System.Net.Quic.QuicException)
        {
            // The console aborted reading it: expected.
        }

        var small = QuicLoopbackHarness.RandomBytes(1024);
        await agent.SendVideoFrameAsync(small, deadline.Token);

        Assert.Equal(small, await master.ReceiveVideoFrameAsync(deadline.Token));
    }

    [Fact(Timeout = 30000)]
    public async Task ABroadcastStartedAndStopped_LeavesTheConnectionOpen_WithPicturesArrivingThroughout()
    {
        using var deadline = QuicLoopbackHarness.Deadline();
        await using var harness = await QuicLoopbackHarness.StartAsync(deadline.Token);
        var (master, agent) = await harness.ConnectAsync(deadline.Token);

        var first = QuicLoopbackHarness.RandomBytes(2048);
        await agent.SendVideoFrameAsync(first, deadline.Token);
        Assert.Equal(first, await master.ReceiveVideoFrameAsync(deadline.Token));

        var (control, agentControlPending) = await QuicLoopbackHarness.OpenControlPairAsync(master, agent, deadline.Token);
        await control.SendAsync(new StartBroadcast(1920, 1080, 15), deadline.Token);
        var agentControl = await agentControlPending;
        Assert.IsType<StartBroadcast>(await agentControl.ReceiveAsync(deadline.Token));

        var second = QuicLoopbackHarness.RandomBytes(2048);
        await agent.SendVideoFrameAsync(second, deadline.Token);
        Assert.Equal(second, await master.ReceiveVideoFrameAsync(deadline.Token));

        await control.SendAsync(new StopBroadcast(), deadline.Token);
        Assert.IsType<StopBroadcast>(await agentControl.ReceiveAsync(deadline.Token));

        var third = QuicLoopbackHarness.RandomBytes(2048);
        await agent.SendVideoFrameAsync(third, deadline.Token);
        Assert.Equal(third, await master.ReceiveVideoFrameAsync(deadline.Token));
    }

    [Fact(Timeout = 30000)]
    public async Task TheConsolesReceive_EndsWhenTheAgentCloses()
    {
        using var deadline = QuicLoopbackHarness.Deadline();
        await using var harness = await QuicLoopbackHarness.StartAsync(deadline.Token);
        var (master, agent) = await harness.ConnectAsync(deadline.Token);

        await agent.DisposeAsync();

        await Assert.ThrowsAnyAsync<Exception>(async () => await master.ReceiveVideoFrameAsync(deadline.Token));
    }
}
