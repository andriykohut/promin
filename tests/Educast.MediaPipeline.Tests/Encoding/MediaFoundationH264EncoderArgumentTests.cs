using Educast.MediaPipeline.Encoding;

namespace Educast.MediaPipeline.Tests.Encoding;

// Argument checks that run before anything native is touched, so they need no GPU.
public class MediaFoundationH264EncoderArgumentTests
{
    [Theory]
    [InlineData(1281, 720)]  // odd width
    [InlineData(1280, 719)]  // odd height
    [InlineData(0, 720)]
    [InlineData(1280, 0)]
    [InlineData(-2, 720)]
    [InlineData(1280, -2)]
    public void Constructor_RefusesASizeThatIsOddOrNotPositive(int width, int height)
    {
        // The size is judged before the device, so a null device is enough to reach the check.
        var ex = Assert.Throws<ArgumentException>(() => new MediaFoundationH264Encoder(null!, width, height, targetFps: 15, bitsPerSecond: 1_000_000));

        Assert.Equal(width <= 0 || width % 2 != 0 ? "width" : "height", ex.ParamName);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(241)]
    public void Constructor_RefusesAFrameRateOutsideOneTo240(int fps)
    {
        var ex = Assert.Throws<ArgumentException>(() => new MediaFoundationH264Encoder(null!, 1280, 720, fps, 1_000_000));

        Assert.Equal("targetFps", ex.ParamName);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void Constructor_RefusesABitrateThatIsNotPositive(int bitsPerSecond)
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => new MediaFoundationH264Encoder(null!, 1280, 720, 15, bitsPerSecond));

        Assert.Equal("bitsPerSecond", ex.ParamName);
    }

    [Fact]
    public void Constructor_RefusesANullDevice() =>
        Assert.Throws<ArgumentNullException>(() => new MediaFoundationH264Encoder(null!, 1280, 720, 15, 1_000_000));

    [Fact]
    public void ValidateEncodeSize_AcceptsEvenPositiveSizes_IncludingTheOnesFrameSizingProduces()
    {
        foreach (var (w, h) in new[] { (1920, 1080), (1366, 768), (1920, 802), (606, 1080), (1280, 718), (2, 2) })
            MediaFoundationH264Encoder.ValidateEncodeSize(w, h);
    }
}
