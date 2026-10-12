using Promin.Agent;

namespace Promin.Agent.Tests;

public class RestartBackoffTests
{
    [Fact]
    public void NextDelay_DoublesFromTwoSecondsAndCapsAtOneMinute()
    {
        var backoff = new RestartBackoff();
        var seconds = new List<double>();
        for (var i = 0; i < 8; i++)
        {
            seconds.Add(backoff.NextDelay().TotalSeconds);
        }

        Assert.Equal(new double[] { 2, 4, 8, 16, 32, 60, 60, 60 }, seconds);
    }

    [Fact]
    public void Reset_StartsTheDelaysOver()
    {
        var backoff = new RestartBackoff();
        backoff.NextDelay();
        backoff.NextDelay();

        backoff.Reset();

        Assert.Equal(2, backoff.NextDelay().TotalSeconds);
    }
}
