using System.Diagnostics;
using Promin.Protocol.Broadcast;
using Promin.Protocol.Messages;
using Promin.Protocol.Pairing;
using Promin.Protocol.Tests.TestDoubles;

namespace Promin.Protocol.Tests.Broadcast;

public class ConsoleControlTests
{
    private static readonly StartBroadcast Start = new(1920, 1080, 15);

    /// <summary>Counts what the console side has read, so a test knows a message was taken and dropped.</summary>
    private sealed class Counting(IControlChannel inner) : IControlChannel
    {
        private int _received;

        public int Received => Volatile.Read(ref _received);

        public Task SendAsync(ControlMessage message, CancellationToken ct) => inner.SendAsync(message, ct);

        public async Task<ControlMessage> ReceiveAsync(CancellationToken ct)
        {
            var message = await inner.ReceiveAsync(ct);
            Interlocked.Increment(ref _received);
            return message;
        }
    }

    private sealed class Ending : IControlChannel
    {
        public TaskCompletionSource End { get; } = new();

        public Task SendAsync(ControlMessage message, CancellationToken ct) => Task.CompletedTask;

        public async Task<ControlMessage> ReceiveAsync(CancellationToken ct)
        {
            await End.Task.WaitAsync(ct);
            throw new IOException("The stream ended.");
        }
    }

    private static async Task Eventually(Func<bool> condition, string what)
    {
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            if (clock.Elapsed > TimeSpan.FromSeconds(5))
            {
                Assert.Fail($"Timed out waiting for: {what}");
            }

            await Task.Delay(1);
        }
    }

    [Fact(Timeout = 10000)]
    public async Task Start_Completes_WhenTheAgentAnswersReady()
    {
        var (console, agent) = InMemoryControlChannel.CreatePair();
        await using var control = new ConsoleControl(console);

        var start = control.StartAsync(Start, TestContext.Current.CancellationToken);
        Assert.IsType<StartBroadcast>(await agent.ReceiveAsync(TestContext.Current.CancellationToken));
        Assert.False(start.IsCompleted);

        await agent.SendAsync(new BroadcastReady(), TestContext.Current.CancellationToken);
        await start;
    }

    [Fact(Timeout = 10000)]
    public async Task AReadyNobodyWaitsFor_IsDropped_NotTakenAsTheNextStartsAnswer()
    {
        var (console, agent) = InMemoryControlChannel.CreatePair();
        var counting = new Counting(console);
        await using var control = new ConsoleControl(counting);

        // The answer to a start re-sent after the display changed size.
        await agent.SendAsync(new BroadcastReady(), TestContext.Current.CancellationToken);
        await Eventually(() => counting.Received == 1, "the stale answer read");

        var start = control.StartAsync(Start, TestContext.Current.CancellationToken);
        await agent.ReceiveAsync(TestContext.Current.CancellationToken);
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.False(start.IsCompleted);

        await agent.SendAsync(new BroadcastReady(), TestContext.Current.CancellationToken);
        await start;
    }

    [Fact(Timeout = 10000)]
    public async Task Start_Throws_WhenTheStreamEnds_AndEveryLaterStartThrowsAtOnce()
    {
        var ending = new Ending();
        await using var control = new ConsoleControl(ending);

        var start = control.StartAsync(Start, TestContext.Current.CancellationToken);
        ending.End.SetResult();

        await Assert.ThrowsAsync<InvalidOperationException>(() => start);
        await Assert.ThrowsAsync<InvalidOperationException>(() => control.StartAsync(Start, TestContext.Current.CancellationToken));
    }

    [Fact(Timeout = 10000)]
    public async Task Send_ReachesTheAgent()
    {
        var (console, agent) = InMemoryControlChannel.CreatePair();
        await using var control = new ConsoleControl(console);

        await control.SendAsync(new StopBroadcast(), TestContext.Current.CancellationToken);

        Assert.IsType<StopBroadcast>(await agent.ReceiveAsync(TestContext.Current.CancellationToken));
    }
}
