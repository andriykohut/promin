using Promin.Master.Core.Wall;

namespace Promin.Master.Core.Tests.Wall;

public class RecentDropsTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 3, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void NothingObserved_IsNotDropping()
    {
        var drops = new RecentDrops(TimeSpan.FromSeconds(10));

        Assert.False(drops.IsDropping(T0));
        Assert.Equal(0, drops.Total);
    }

    [Fact]
    public void ARisingTotal_IsDropping_ForTenSeconds()
    {
        var drops = new RecentDrops(TimeSpan.FromSeconds(10));
        drops.Observe(0, T0);

        drops.Observe(3, T0.AddSeconds(60));

        Assert.True(drops.IsDropping(T0.AddSeconds(60)));
        Assert.True(drops.IsDropping(T0.AddSeconds(69.9)));
        Assert.False(drops.IsDropping(T0.AddSeconds(70)));
    }

    [Fact]
    public void OneEarlyDrop_DoesNotMarkThePcForTheWholeLesson()
    {
        var drops = new RecentDrops(TimeSpan.FromSeconds(10));
        drops.Observe(1, T0);

        for (var second = 1; second <= 2700; second++)
        {
            drops.Observe(1, T0.AddSeconds(second));
        }

        Assert.False(drops.IsDropping(T0.AddSeconds(2700)));
        Assert.Equal(1, drops.Total); // the total is still reported in the details panel
    }

    [Fact]
    public void DroppingAgain_RestartsTheWindow()
    {
        var drops = new RecentDrops(TimeSpan.FromSeconds(10));
        drops.Observe(3, T0);
        drops.Observe(3, T0.AddSeconds(30));

        drops.Observe(4, T0.AddSeconds(100));

        Assert.True(drops.IsDropping(T0.AddSeconds(105)));
    }

    [Fact]
    public void Reset_ClearsTheTotalAndTheWindow()
    {
        var drops = new RecentDrops(TimeSpan.FromSeconds(10));
        drops.Observe(5, T0);

        drops.Reset();

        Assert.False(drops.IsDropping(T0));
        Assert.Equal(0, drops.Total);
    }
}
