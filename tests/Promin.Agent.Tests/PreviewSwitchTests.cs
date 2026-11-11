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
}
