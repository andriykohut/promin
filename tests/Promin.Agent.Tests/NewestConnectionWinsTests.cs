namespace Promin.Agent.Tests;

public class NewestConnectionWinsTests
{
    [Fact]
    public async Task ANewerConnection_CancelsTheOlderOne_AndStartsOnlyAfterItHasFinished()
    {
        var slot = new NewestConnectionWins();
        var events = new List<string>();
        var olderStopping = new TaskCompletionSource();

        await slot.StartAsync(async ct =>
        {
            events.Add("older started");
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
                events.Add("older cancelled");
                await olderStopping.Task; // tearing down its display takes a moment
                events.Add("older finished");
            }
        }, CancellationToken.None);

        var newer = slot.StartAsync(ct =>
        {
            events.Add("newer started");
            return Task.CompletedTask;
        }, CancellationToken.None);

        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.DoesNotContain("newer started", events); // the older one has not finished yet

        olderStopping.SetResult();
        await newer;
        await slot.StopAsync();

        Assert.Equal(["older started", "older cancelled", "older finished", "newer started"], events);
    }

    [Fact]
    public async Task AnOlderConnectionThatFailed_DoesNotStopTheNewerOneStarting()
    {
        var slot = new NewestConnectionWins();
        await slot.StartAsync(_ => throw new InvalidOperationException("boom"), CancellationToken.None);
        var ran = new TaskCompletionSource();

        await slot.StartAsync(_ =>
        {
            ran.SetResult();
            return Task.CompletedTask;
        }, CancellationToken.None);

        await ran.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Stop_CancelsTheCurrentOne_AndWaitsForIt()
    {
        var slot = new NewestConnectionWins();
        var finished = false;
        await slot.StartAsync(async ct =>
        {
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
                await Task.Delay(50);
                finished = true;
            }
        }, CancellationToken.None);

        await slot.StopAsync();

        Assert.True(finished);
    }

    [Fact]
    public async Task Stop_WithNothingRunning_Returns()
    {
        await new NewestConnectionWins().StopAsync();
    }

    [Fact]
    public async Task TheCallersTokenCancelsTheCurrentOneToo()
    {
        var slot = new NewestConnectionWins();
        using var shutdown = new CancellationTokenSource();
        var cancelled = new TaskCompletionSource();
        await slot.StartAsync(async ct =>
        {
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
                cancelled.SetResult();
            }
        }, shutdown.Token);

        await shutdown.CancelAsync();

        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }
}
