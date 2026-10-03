using Educast.MediaPipeline.Encoding;

namespace Educast.MediaPipeline.Tests.Encoding;

public class EncoderSettingsTests
{
    [Theory]
    [InlineData(1920, 1080, 15, 3_110_400u)]
    [InlineData(1920, 1080, 10, 2_073_600u)]
    [InlineData(1280, 720, 10, 921_600u)]
    [InlineData(1920, 802, 15, 2_309_760u)]
    public void BitrateFor_IsATenthOfABitPerPixelPerFrame(int width, int height, int fps, uint expected) =>
        Assert.Equal(expected, EncoderSettings.BitrateFor(width, height, fps));

    [Fact]
    public void BitrateFor_NeverFallsBelowTheFloor() =>
        Assert.Equal(EncoderSettings.MinimumBitrate, EncoderSettings.BitrateFor(64, 64, 1));

    [Fact]
    public void BitrateFor_NeverExceedsTheCeiling_EvenForHugeFrames() =>
        Assert.Equal(EncoderSettings.MaximumBitrate, EncoderSettings.BitrateFor(7680, 4320, 60));

    [Theory]
    [InlineData(15, 75u)]
    [InlineData(10, 50u)]
    [InlineData(1, 5u)]
    public void KeyframeInterval_IsFiveSecondsOfFrames(int fps, uint frames) =>
        Assert.Equal(frames, EncoderSettings.KeyframeIntervalFor(fps));

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
}
