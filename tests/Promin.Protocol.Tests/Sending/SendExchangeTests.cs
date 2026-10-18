using System.Net.Quic;
using Promin.Protocol.Messages;
using Promin.Protocol.Sending;
using Promin.Protocol.Tests.TestDoubles;
using Promin.Protocol.Tests.Transport;
using Promin.Protocol.Transport;

namespace Promin.Protocol.Tests.Sending;

/// <summary>
/// Both ends of one send over real QUIC: the console's end writing while the agent's end
/// reads, as each PC runs them. QUIC flow control holds the console's bytes back until the
/// agent reads, so the two always run at the same time.
/// </summary>
public class SendExchangeTests
{
    private const int MiB = 1024 * 1024;

    [Fact(Timeout = 30000)]
    public async Task AWholeSend_ArrivesInOrder_AndTheAgentsAnswerIsReturned()
    {
        using var deadline = QuicLoopbackHarness.Deadline();
        await using var harness = await QuicLoopbackHarness.StartAsync(deadline.Token);
        var (master, agentPending) = await OpenAsync(harness, deadline.Token);
        await using var ownedMaster = master;
        var quiz = QuicLoopbackHarness.RandomBytes(48_000);
        var png = QuicLoopbackHarness.RandomBytes(10_000);
        var target = new MemoryReceiveTarget();

        var sending = Task.Run(() => SendExchange.SendAsync(
            master,
            [FileItem("quiz.html", quiz), FolderItem("quiz_files"), FileItem("quiz_files/a.png", png), FileItem("quiz_files/empty.txt", [])],
            open: true, _ => { }, deadline.Token));
        var (header, answered) = await ReceiveAsync(agentPending, target, deadline.Token);

        Assert.Equal(SendOutcome.Sent, await sending);
        Assert.Equal(SendOutcome.Sent, answered);
        Assert.Equal(new SendFiles(58_000, true), header);
        Assert.Equal(58_000, target.Total);
        (string, bool)[] expected =
            [("quiz.html", false), ("quiz_files", true), ("quiz_files/a.png", false), ("quiz_files/empty.txt", false)];
        Assert.Equal(expected, target.Items);
        Assert.Equal(quiz, target.Files["quiz.html"].ToArray());
        Assert.Equal(png, target.Files["quiz_files/a.png"].ToArray());
        Assert.Empty(target.Files["quiz_files/empty.txt"].ToArray());
        Assert.True(target.Finished);
        Assert.False(target.Discarded);
    }

    [Fact(Timeout = 30000)]
    public async Task TwoThousandSmallFiles_AllArrive()
    {
        using var deadline = QuicLoopbackHarness.Deadline();
        await using var harness = await QuicLoopbackHarness.StartAsync(deadline.Token);
        var (master, agentPending) = await OpenAsync(harness, deadline.Token);
        await using var ownedMaster = master;
        var files = Enumerable.Range(0, 2_000)
            .Select(i => (Path: $"many/{i}.txt", Bytes: QuicLoopbackHarness.RandomBytes(100)))
            .ToList();
        var target = new MemoryReceiveTarget();

        var sending = Task.Run(() => SendExchange.SendAsync(
            master, [FolderItem("many"), .. files.Select(f => FileItem(f.Path, f.Bytes))],
            open: false, _ => { }, deadline.Token));
        await ReceiveAsync(agentPending, target, deadline.Token);

        Assert.Equal(SendOutcome.Sent, await sending);
        Assert.Equal(2_000, target.Files.Count);
        Assert.All(files, f => Assert.Equal(f.Bytes, target.Files[f.Path].ToArray()));
    }

    [Fact(Timeout = 30000)]
    public async Task WrittenReportsEveryByte()
    {
        using var deadline = QuicLoopbackHarness.Deadline();
        await using var harness = await QuicLoopbackHarness.StartAsync(deadline.Token);
        var (master, agentPending) = await OpenAsync(harness, deadline.Token);
        await using var ownedMaster = master;
        long lastWritten = 0;

        var sending = Task.Run(() => SendExchange.SendAsync(
            master,
            [FileItem("a.bin", QuicLoopbackHarness.RandomBytes(200_000)), FileItem("b.bin", QuicLoopbackHarness.RandomBytes(70_000))],
            open: false, n => lastWritten = n, deadline.Token));
        await ReceiveAsync(agentPending, new MemoryReceiveTarget(), deadline.Token);

        Assert.Equal(SendOutcome.Sent, await sending);
        Assert.Equal(270_000, lastWritten);
    }

    /// <summary>
    /// A refusal at the start reaches the console while it is still writing, and stops it.
    /// Fails a console that writes every byte before it reads the answer.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task AnEarlyRefusal_StopsTheConsoleBeforeItHasWrittenEverything()
    {
        using var deadline = QuicLoopbackHarness.Deadline();
        await using var harness = await QuicLoopbackHarness.StartAsync(deadline.Token);
        var (master, agentPending) = await OpenAsync(harness, deadline.Token);
        await using var ownedMaster = master;
        var target = new MemoryReceiveTarget { RefuseAtBegin = SendOutcome.NotEnoughSpace };
        long lastWritten = 0;

        var sending = Task.Run(() => SendExchange.SendAsync(
            master, [FileItem("big.bin", new byte[20 * MiB])], open: false, n => lastWritten = n, deadline.Token));
        var (_, answered) = await ReceiveAsync(agentPending, target, deadline.Token);

        Assert.Equal(SendOutcome.NotEnoughSpace, await sending);
        Assert.Equal(SendOutcome.NotEnoughSpace, answered);
        Assert.True(lastWritten < 20 * MiB, $"The console wrote {lastWritten} bytes.");
        Assert.True(target.Discarded);
    }

    [Fact(Timeout = 30000)]
    public async Task ABadPathOnTheSecondItem_IsRefused_AndTheTargetDiscards()
    {
        using var deadline = QuicLoopbackHarness.Deadline();
        await using var harness = await QuicLoopbackHarness.StartAsync(deadline.Token);
        var (master, agentPending) = await OpenAsync(harness, deadline.Token);
        await using var ownedMaster = master;
        var target = new MemoryReceiveTarget { RefuseAtItem = (2, SendOutcome.BadPath) };

        var sending = Task.Run(() => SendExchange.SendAsync(
            master,
            [FileItem("a.txt", QuicLoopbackHarness.RandomBytes(10)), FileItem("../b.txt", QuicLoopbackHarness.RandomBytes(10))],
            open: false, _ => { }, deadline.Token));
        var (_, answered) = await ReceiveAsync(agentPending, target, deadline.Token);

        Assert.Equal(SendOutcome.BadPath, await sending);
        Assert.Equal(SendOutcome.BadPath, answered);
        Assert.True(target.Discarded);
        Assert.False(target.Finished);
    }

    [Fact(Timeout = 30000)]
    public async Task CancellingOnTheConsole_EndsTheAgentsReceive_AndTheTargetDiscards()
    {
        using var deadline = QuicLoopbackHarness.Deadline();
        await using var harness = await QuicLoopbackHarness.StartAsync(deadline.Token);
        var (master, agentPending) = await OpenAsync(harness, deadline.Token);
        await using var ownedMaster = master;
        var target = new MemoryReceiveTarget();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);

        // Cancelled at the first chunk after the agent has begun, so the abort ends the
        // exchange and not the agent's read of the header. Flow control keeps the console from
        // writing the 50 MiB before then.
        var sending = Task.Run(() => SendExchange.SendAsync(
            master, [FileItem("big.bin", new byte[50 * MiB])], open: false,
            _ =>
            {
                if (target.Began.IsCompleted)
                {
                    stop.Cancel();
                }
            },
            stop.Token));
        var receiving = ReceiveAsync(agentPending, target, deadline.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sending);
        await Assert.ThrowsAsync<QuicException>(() => receiving);
        Assert.True(target.Discarded);
        Assert.False(deadline.IsCancellationRequested);
    }

    [Fact(Timeout = 30000)]
    public async Task ASourceThatEndsEarly_ThrowsSendSourceException_NamingIt()
    {
        using var deadline = QuicLoopbackHarness.Deadline();
        await using var harness = await QuicLoopbackHarness.StartAsync(deadline.Token);
        var (master, agentPending) = await OpenAsync(harness, deadline.Token);
        await using var ownedMaster = master;
        var shortItem = new OutgoingItem(
            "short.bin", 1_000, IsFolder: false, () => new MemoryStream(QuicLoopbackHarness.RandomBytes(10)));

        var sending = Task.Run(() => SendExchange.SendAsync(
            master, [shortItem], open: false, _ => { }, deadline.Token));
        var receiving = ReceiveAsync(agentPending, new MemoryReceiveTarget(), deadline.Token);

        var error = await Assert.ThrowsAsync<SendSourceException>(() => sending);
        Assert.Equal("short.bin", error.Path);
        await Assert.ThrowsAsync<QuicException>(() => receiving);
    }

    /// <summary>
    /// A console that promises fewer bytes than it sends could fill the disk past what the
    /// free-space check allowed. Fails an agent that takes bytes beyond the header's total.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task AHeaderThatUnderstatesTheBytes_IsRefusedAsFailed()
    {
        using var deadline = QuicLoopbackHarness.Deadline();
        await using var harness = await QuicLoopbackHarness.StartAsync(deadline.Token);
        var (master, agentPending) = await OpenAsync(harness, deadline.Token);
        await using var ownedMaster = master;
        var target = new MemoryReceiveTarget();

        await master.SendAsync(new SendFiles(10, false), deadline.Token);
        await master.SendAsync(new SendItem("a", 20, false), deadline.Token);
        await master.SendBytesAsync(QuicLoopbackHarness.RandomBytes(20), deadline.Token);
        var (_, answered) = await ReceiveAsync(agentPending, target, deadline.Token);

        Assert.Equal(SendOutcome.Failed, answered);
        Assert.Equal(new SendDone(SendOutcome.Failed), await master.ReceiveAsync(deadline.Token));
        Assert.Empty(target.Files);
        Assert.True(target.Discarded);
        Assert.False(target.Finished);
    }

    private static OutgoingItem FileItem(string path, byte[] bytes) =>
        new(path, bytes.Length, IsFolder: false, () => new MemoryStream(bytes));

    private static OutgoingItem FolderItem(string path) => new(path, 0, IsFolder: true, Open: null);

    /// <summary>Connects, and opens the stream a send runs on.</summary>
    private static async Task<(QuicControlChannel Master, Task<QuicControlChannel> AgentPending)> OpenAsync(
        QuicLoopbackHarness harness, CancellationToken ct)
    {
        var (master, agent) = await harness.ConnectAsync(ct);
        return await QuicLoopbackHarness.OpenControlPairAsync(master, agent, ct);
    }

    /// <summary>
    /// The agent's end as the owner of its stream runs it: reads the header, runs the exchange,
    /// then disposes the stream.
    /// </summary>
    private static async Task<(SendFiles Header, SendOutcome Outcome)> ReceiveAsync(
        Task<QuicControlChannel> agentPending, IReceiveTarget target, CancellationToken ct)
    {
        await using var agent = await agentPending;
        var header = Assert.IsType<SendFiles>(await agent.ReceiveAsync(ct));
        return (header, await SendExchange.ReceiveAsync(agent, header, target, ct));
    }
}
