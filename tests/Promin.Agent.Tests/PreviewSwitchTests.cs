using System.Diagnostics;

namespace Promin.Agent.Tests;

public class PreviewSwitchTests
{
    [Fact]
    public async Task RunsFromTheStart_StopsOnPause_AndRunsAgainOnResume()
    {
        var calls = new List<string>();
        await using var previews = new PreviewSwitch(async ct =>
        {
            calls.Add("run");
            await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            calls.Add("released");
        });

        await previews.PauseAsync();
        previews.Resume();
        await previews.PauseAsync();

        Assert.Equal(["run", "released", "run", "released"], calls);
    }

    [Fact]
    public async Task ARunThatEndsByItself_RunsAgainOnResume_AndPauseStillWorks()
    {
        var runs = 0;
        await using var previews = new PreviewSwitch(_ =>
        {
            Interlocked.Increment(ref runs);
            return Task.CompletedTask;
        });

        var clock = Stopwatch.StartNew();
        while (Volatile.Read(ref runs) < 2)
        {
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), "Resume never ran the pictures again after they ended by themselves.");
            previews.Resume();
            await Task.Delay(1, TestContext.Current.CancellationToken);
        }

        await previews.PauseAsync();
        await previews.PauseAsync();
    }
}
