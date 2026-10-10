using Promin.Protocol.Video;

namespace Promin.Protocol.Tests.Video;

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

    [Theory]
    // picture          screen            x    y    width height
    [InlineData(1920, 1080, 1920, 1080,   0,   0, 1920, 1080)] // same size: fills
    [InlineData(1920, 1080, 1280, 1024,   0, 152, 1280,  720)] // onto 5:4: bars above and below
    [InlineData(1280, 1024, 1920, 1080, 285,   0, 1350, 1080)] // 5:4 onto 16:9: bars left and right
    [InlineData(1920,  802, 1920, 1080,   0, 139, 1920,  802)] // ultrawide picture on 16:9
    [InlineData(1920, 1080, 1080, 1920,   0, 656, 1080,  607)] // onto a portrait screen
    [InlineData(1366,  768, 3840, 2160,   0,   1, 3840, 2158)] // small picture scaled up
    [InlineData(1920, 1080, 1366,  768,   0,   0, 1365,  768)] // 1366x768 is a hair wider than 16:9
    public void FitRect_ShowsTheWholePicture_Centred(
        int pictureWidth, int pictureHeight, int screenWidth, int screenHeight, int x, int y, int width, int height) =>
        Assert.Equal(
            new PixelRect(x, y, width, height),
            FrameSizing.FitRect(new(pictureWidth, pictureHeight), new(screenWidth, screenHeight)));

    [Theory]
    [InlineData(1920, 1080, 1280, 1024)]
    [InlineData(606, 1080, 1920, 1080)]
    [InlineData(3840, 2160, 1024, 768)]
    public void FitRect_NeverLeavesTheScreen(int pictureWidth, int pictureHeight, int screenWidth, int screenHeight)
    {
        var rect = FrameSizing.FitRect(new(pictureWidth, pictureHeight), new(screenWidth, screenHeight));

        Assert.True(rect.X >= 0 && rect.Y >= 0);
        Assert.True(rect.X + rect.Width <= screenWidth);
        Assert.True(rect.Y + rect.Height <= screenHeight);
        Assert.True(rect.Width == screenWidth || rect.Height == screenHeight); // touches two opposite edges
    }

    [Theory]
    [InlineData(0, 1080, 1920, 1080)]
    [InlineData(1920, 0, 1920, 1080)]
    [InlineData(1920, 1080, 0, 1080)]
    [InlineData(1920, 1080, 1920, -1)]
    public void FitRect_RefusesASizeThatIsNotPositive(int pictureWidth, int pictureHeight, int screenWidth, int screenHeight) =>
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            FrameSizing.FitRect(new(pictureWidth, pictureHeight), new(screenWidth, screenHeight)));
}
