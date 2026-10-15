using Promin.MediaPipeline.Pictures;
using Promin.Protocol.Video;

namespace Promin.MediaPipeline.Tests.Pictures;

public class PictureSizingTests
{
    [Theory]
    [InlineData(1920, 1080, 480, 270)]   // 16:9
    [InlineData(2560, 1600, 480, 300)]   // 16:10 fills the bounds exactly
    [InlineData(1280, 1024, 375, 300)]   // 5:4, the old office monitor
    [InlineData(1080, 1920, 169, 300)]   // portrait
    [InlineData(5120, 1440, 480, 135)]   // ultrawide
    [InlineData(3840, 2160, 480, 270)]   // 4K
    public void Fit_KeepsTheAspect_InsideTheBounds(int width, int height, int expectedWidth, int expectedHeight)
    {
        var fitted = PictureSizing.Fit(new PixelSize(width, height));

        Assert.Equal(new PixelSize(expectedWidth, expectedHeight), fitted);
        Assert.InRange(fitted.Width, 1, PictureSizing.Bounds.Width);
        Assert.InRange(fitted.Height, 1, PictureSizing.Bounds.Height);
    }

    [Theory]
    [InlineData(400, 250)]
    [InlineData(1, 1)]
    public void Fit_NeverEnlargesASmallScreen(int width, int height) =>
        Assert.Equal(new PixelSize(width, height), PictureSizing.Fit(new PixelSize(width, height)));

    [Fact]
    public void Fit_RefusesAnEmptySize() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => PictureSizing.Fit(new PixelSize(0, 1080)));
}
