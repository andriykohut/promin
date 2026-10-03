using Educast.Protocol.Video;

namespace Educast.Protocol.Tests.Video;

public class FrameSizingTests
{
    private static readonly PixelSize Hd = new(1920, 1080);
    private static readonly PixelSize Sd = new(1280, 720);

    [Theory]
    [InlineData(1920, 1080, 1920, 1080)] // already the ceiling
    [InlineData(1366, 768, 1366, 768)]   // small laptop: never scaled up
    [InlineData(1280, 1024, 1280, 1024)] // 5:4, fits
    [InlineData(2560, 1440, 1920, 1080)] // 1440p
    [InlineData(3840, 2160, 1920, 1080)] // 4K
    [InlineData(3440, 1440, 1920, 802)]  // ultrawide: bound by width; 803 rounded down to even
    [InlineData(1920, 1200, 1728, 1080)] // 16:10: bound by height
    [InlineData(1080, 1920, 606, 1080)]  // portrait: 607 rounded down to even
    [InlineData(1365, 767, 1364, 766)]   // odd source made even
    public void EncodeSize_Under1080pCeiling(int sourceWidth, int sourceHeight, int width, int height) =>
        Assert.Equal(new PixelSize(width, height), FrameSizing.EncodeSize(new(sourceWidth, sourceHeight), Hd));

    [Theory]
    [InlineData(1920, 1080, 1280, 720)]
    [InlineData(1366, 768, 1280, 718)]   // 719 rounded down to even
    [InlineData(1024, 768, 960, 720)]
    public void EncodeSize_Under720pCeiling(int sourceWidth, int sourceHeight, int width, int height) =>
        Assert.Equal(new PixelSize(width, height), FrameSizing.EncodeSize(new(sourceWidth, sourceHeight), Sd));

    [Theory]
    [InlineData(0, 1080)]
    [InlineData(1920, 0)]
    [InlineData(-1, 1080)]
    public void EncodeSize_RefusesANonPositiveSource(int width, int height) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => FrameSizing.EncodeSize(new(width, height), Hd));
}
