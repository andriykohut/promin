using Promin.Protocol.Video;

namespace Promin.Protocol.Tests.Video;

public class VideoBitrateTests
{
    [Theory]
    [InlineData(1920, 1080, 15, Sharpness.Light, 3_110_400)]
    [InlineData(1920, 1080, 15, Sharpness.Standard, 6_220_800)]
    [InlineData(1920, 1080, 15, Sharpness.Sharp, 9_331_200)]
    [InlineData(1920, 1080, 10, Sharpness.Light, 2_073_600)]
    [InlineData(1920, 1080, 10, Sharpness.Standard, 4_147_200)]
    [InlineData(1920, 1080, 10, Sharpness.Sharp, 6_220_800)]
    [InlineData(1280, 720, 15, Sharpness.Light, 1_382_400)]
    [InlineData(1280, 720, 15, Sharpness.Standard, 2_764_800)]
    [InlineData(1280, 720, 15, Sharpness.Sharp, 4_147_200)]
    [InlineData(1280, 720, 10, Sharpness.Light, 921_600)]
    [InlineData(1280, 720, 10, Sharpness.Standard, 1_843_200)]
    [InlineData(1280, 720, 10, Sharpness.Sharp, 2_764_800)]
    [InlineData(1366, 768, 15, Sharpness.Light, 1_573_632)]
    [InlineData(1366, 768, 15, Sharpness.Standard, 3_147_264)]
    [InlineData(1366, 768, 15, Sharpness.Sharp, 4_720_896)]
    public void BitsPerSecond_IsTenthsOfABitPerPixelPerFrame(int width, int height, int fps, Sharpness sharpness, int expected) =>
        Assert.Equal(expected, VideoBitrate.BitsPerSecond(new PixelSize(width, height), fps, sharpness));

    [Theory]
    [InlineData(0, 1080, 15)]
    [InlineData(1920, 0, 15)]
    [InlineData(-2, 1080, 15)]
    [InlineData(1920, 1080, 0)]
    [InlineData(1920, 1080, -1)]
    public void BitsPerSecond_RefusesANonPositiveSizeOrFrameRate(int width, int height, int fps) =>
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            VideoBitrate.BitsPerSecond(new PixelSize(width, height), fps, Sharpness.Standard));

    [Fact]
    public void BitsPerSecond_RefusesASharpnessThatIsNotOneOfTheThree() =>
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            VideoBitrate.BitsPerSecond(new PixelSize(1920, 1080), 15, (Sharpness)99));

    [Fact]
    public void DefaultSharpness_IsStandard() =>
        Assert.Equal(Sharpness.Standard, VideoBitrate.DefaultSharpness);

    [Fact]
    public void BitsPerSecond_UsesSixtyFourBitArithmetic_SoAHugeFrameDoesNotWrapAround()
    {
        // 8192x8192 at 240 fps, Sharp: 4.8e10 bits per second does not fit an int, and must not come back as a small number.
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            VideoBitrate.BitsPerSecond(new PixelSize(8192, 8192), 240, Sharpness.Sharp));
    }
}
