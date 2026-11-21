using Promin.Protocol.Messages;
using Promin.Protocol.Updates;

namespace Promin.Protocol.Tests.Transport;

/// <summary>The console's end of the update stream, over real QUIC, against an agent end written out in each test.</summary>
public sealed class QuicUpdateStreamTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("promin-update-stream-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static ReleaseVersion Parse(string text) =>
        ReleaseVersion.TryParse(text, out var version) ? version : throw new FormatException(text);

    [Fact(Timeout = 30000)]
    public async Task AsksTheVersion()
    {
        using var deadline = QuicLoopbackHarness.Deadline(TestContext.Current.CancellationToken);
        await using var harness = await QuicLoopbackHarness.StartAsync(deadline.Token);
        var (master, agent) = await harness.ConnectAsync(deadline.Token);
        var (console, agentPending) = await QuicLoopbackHarness.OpenControlPairAsync(master, agent, deadline.Token);
        await using var ownedConsole = console;

        var asking = UpdateExchange.AskVersionAsync(console, deadline.Token);
        await using var agentEnd = await agentPending;
        Assert.IsType<AskVersion>(await agentEnd.ReceiveAsync(deadline.Token));
        await agentEnd.SendAsync(new AgentVersion("0.3.0"), deadline.Token);

        Assert.Equal(Parse("0.3.0"), await asking);
    }

    [Fact(Timeout = 30000)]
    public async Task AnUnreadableVersionIsNull()
    {
        using var deadline = QuicLoopbackHarness.Deadline(TestContext.Current.CancellationToken);
        await using var harness = await QuicLoopbackHarness.StartAsync(deadline.Token);
        var (master, agent) = await harness.ConnectAsync(deadline.Token);
        var (console, agentPending) = await QuicLoopbackHarness.OpenControlPairAsync(master, agent, deadline.Token);
        await using var ownedConsole = console;

        var asking = UpdateExchange.AskVersionAsync(console, deadline.Token);
        await using var agentEnd = await agentPending;
        await agentEnd.ReceiveAsync(deadline.Token);
        await agentEnd.SendAsync(new AgentVersion("garbage"), deadline.Token);

        Assert.Null(await asking);
    }

    [Fact(Timeout = 30000)]
    public async Task SendsTheSetupAndReadsTheAnswer()
    {
        using var deadline = QuicLoopbackHarness.Deadline(TestContext.Current.CancellationToken);
        await using var harness = await QuicLoopbackHarness.StartAsync(deadline.Token);
        var (master, agent) = await harness.ConnectAsync(deadline.Token);
        var (console, agentPending) = await QuicLoopbackHarness.OpenControlPairAsync(master, agent, deadline.Token);
        await using var ownedConsole = console;

        var setup = Path.Combine(_directory, "Promin-Agent-Setup.exe");
        var bytes = QuicLoopbackHarness.RandomBytes(1_048_576);
        File.WriteAllBytes(setup, bytes);
        byte[] signature = [7, 8, 9];

        // QUIC flow control holds the console's writes back until the agent reads, so the two run at once.
        var sending = UpdateExchange.SendAsync(console, setup, Parse("0.4.0"), signature, deadline.Token);
        await using var agentEnd = await agentPending;
        var offer = Assert.IsType<OfferUpdate>(await agentEnd.ReceiveAsync(deadline.Token));
        Assert.Equal(("0.4.0", (long)bytes.Length), (offer.Version, offer.Length));
        Assert.Equal(signature, offer.Signature);
        using var received = new MemoryStream();
        await agentEnd.ReceiveBytesAsync(received, offer.Length, deadline.Token);
        Assert.Equal(bytes, received.ToArray());
        await agentEnd.SendAsync(new UpdateAnswer(UpdateOutcome.NotNewer), deadline.Token);

        Assert.Equal(UpdateOutcome.NotNewer, await sending);
    }
}
