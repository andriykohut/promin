using Promin.MediaPipeline.Encoding;

namespace Promin.MediaPipeline.Tests.Encoding;

public class EncoderSettingsTests
{
    [Fact]
    public void SampleTime_AdvancesByOneFramePeriod_InHundredNanosecondUnits()
    {
        Assert.Equal(0L, EncoderSettings.SampleTime(0, 10));
        Assert.Equal(1_000_000L, EncoderSettings.SampleTime(1, 10));
        Assert.Equal(10_000_000L, EncoderSettings.SampleTime(10, 10));
    }

    [Fact]
    public void SampleTime_DoesNotDriftWhenThePeriodIsNotAWholeNumber()
    {
        // 1/15 s is 666,666.67 units: after 15 frames the clock must read exactly one second.
        Assert.Equal(10_000_000L, EncoderSettings.SampleTime(15, 15));
        Assert.Equal(10_000_000L * 3000 / 15, EncoderSettings.SampleTime(3000, 15));
    }

    [Fact]
    public void Pack_PutsTheFirstValueInTheHighWord()
    {
        Assert.Equal(0x0000_0780_0000_0438UL, EncoderSettings.Pack(1920, 1080));
        Assert.Equal(0x0000_000F_0000_0001UL, EncoderSettings.Pack(15, 1));
    }

    [Fact]
    public void KeyframeSpacingLadder_StartsWithTheLongestSpacingPossible_AndOnlyGetsShorter()
    {
        var ladder = EncoderSettings.KeyframeSpacingLadder;

        Assert.Equal(uint.MaxValue, ladder[0]);
        Assert.True(ladder.Length > 1, "an encoder that refuses the longest spacing needs something to fall back to");
        for (var i = 1; i < ladder.Length; i++)
            Assert.True(ladder[i] < ladder[i - 1]);
        Assert.True(ladder[^1] >= 1000, "even the shortest fallback is far longer than the broadcast session's own two seconds");
    }
}