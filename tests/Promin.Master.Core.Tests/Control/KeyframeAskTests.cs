using Promin.Master.Core.Control;

namespace Promin.Master.Core.Tests.Control;

public class KeyframeAskTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void TheFirstFrame_IsNotAGap()
    {
        Assert.False(new KeyframeAsk().ShouldAsk(7, false, T0));
    }

    [Fact]
    public void ConsecutiveIds_DoNotAsk()
    {
        var ask = new KeyframeAsk();
        ask.ShouldAsk(7, false, T0);
        Assert.False(ask.ShouldAsk(8, false, T0.AddSeconds(1)));
    }

    [Fact]
    public void AGap_Asks()
    {
        var ask = new KeyframeAsk();
        ask.ShouldAsk(7, false, T0);
        Assert.True(ask.ShouldAsk(9, false, T0.AddSeconds(1)));
    }

    [Fact]
    public void ADecoderThatNeedsAKeyframe_Asks()
    {
        Assert.True(new KeyframeAsk().ShouldAsk(1, true, T0));
    }

    [Fact]
    public void TwoAsksWithin500Ms_GiveOne_AndAfterThatItCanAskAgain()
    {
        var ask = new KeyframeAsk();
        Assert.True(ask.ShouldAsk(1, true, T0));
        Assert.False(ask.ShouldAsk(2, true, T0.AddMilliseconds(499)));
        Assert.True(ask.ShouldAsk(3, true, T0.AddMilliseconds(500)));
    }
}
