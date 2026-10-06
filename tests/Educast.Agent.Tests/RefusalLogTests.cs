namespace Educast.Agent.Tests;

public class RefusalLogTests
{
    private sealed class Clock : TimeProvider
    {
        private long _ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => _ticks;

        public void Advance(TimeSpan by) => _ticks += by.Ticks;
    }

    [Fact]
    public void TheFirstRefusalIsSaidAtOnce()
    {
        var lines = new List<string>();
        var log = new RefusalLog(lines.Add, TimeSpan.FromMinutes(1), new Clock());

        log.Record(new InvalidOperationException("not pinned"));

        Assert.Contains("not pinned", Assert.Single(lines));
    }

    [Fact]
    public void ARunOfRefusals_IsOneLineAMinute_WithTheCount()
    {
        var lines = new List<string>();
        var clock = new Clock();
        var log = new RefusalLog(lines.Add, TimeSpan.FromMinutes(1), clock);

        log.Record(new Exception("one"));
        for (var i = 0; i < 1000; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(50)); // 50 seconds in all
            log.Record(new Exception("flood"));
        }

        Assert.Single(lines);

        clock.Advance(TimeSpan.FromSeconds(11));
        log.Record(new Exception("the latest"));

        Assert.Equal(2, lines.Count);
        Assert.Contains("1001 connections", lines[1]);
        Assert.Contains("the latest", lines[1]);
    }

    [Fact]
    public void AfterAReport_TheCountStartsAgain()
    {
        var lines = new List<string>();
        var clock = new Clock();
        var log = new RefusalLog(lines.Add, TimeSpan.FromMinutes(1), clock);
        log.Record(new Exception("one"));
        clock.Advance(TimeSpan.FromMinutes(1));
        log.Record(new Exception("two"));

        log.Record(new Exception("three"));
        clock.Advance(TimeSpan.FromMinutes(1));
        log.Record(new Exception("four"));

        Assert.Equal(3, lines.Count);
        Assert.Contains("2 connections", lines[2]);
    }
}
