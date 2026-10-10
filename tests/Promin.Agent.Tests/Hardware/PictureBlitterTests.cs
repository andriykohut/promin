using Promin.MediaPipeline.Scaling;
using Promin.Protocol.Video;
using Xunit.Abstractions;
using static Promin.Agent.Tests.Hardware.GpuPictures;

namespace Promin.Agent.Tests.Hardware;

// These need the GPU and its video processor. They use synthetic pictures, open no window and capture nothing.
// They cannot run on a headless CI agent: run them by hand on the target machine (remove the Skip text, run,
// put it back). The pictures are made NV12 by the capture side's own converter, so this also checks that what
// the teacher PC's pipeline writes is what the student PC draws.
public class PictureBlitterTests(ITestOutputHelper output)
{
    private const string NeedsGpu =
        "Requires the GPU and its video processor; synthetic pictures, no window, captures nothing; run manually on target hardware.";

    private static readonly Pixel Black = new(0, 0, 0);
    private static readonly Pixel Red = new(0, 0, 255);
    private static readonly Pixel Green = new(0, 255, 0);

    private static bool Near(Pixel actual, Pixel expected, int tolerance) =>
        Math.Abs(actual.Blue - expected.Blue) <= tolerance
        && Math.Abs(actual.Green - expected.Green) <= tolerance
        && Math.Abs(actual.Red - expected.Red) <= tolerance;

    private static string Show(Pixel p) => $"(B{p.Blue} G{p.Green} R{p.Red})";

    [Fact(Skip = NeedsGpu)]
    public void AWidePictureOnATallerScreen_IsCentred_WithBlackBarsAboveAndBelow()
    {
        using var gpu = GpuDevice.Create();
        output.WriteLine($"adapter: {gpu.AdapterName}");
        using var converter = new GpuFrameConverter(gpu.Device);
        using var blitter = new PictureBlitter(gpu.Device);
        using var source = Solid(gpu.Device, 1920, 1080, Red);
        var picture = converter.Convert(source, 1920, 1080, 1920, 1080);
        using var screen = RenderTarget(gpu.Device, 1280, 1024);

        blitter.Draw(picture, 1920, 1080, screen, new PixelSize(1280, 1024));
        var at = Read(gpu.Device, screen, 1280, 1024);

        // FitRect says (0, 152, 1280, 720).
        Assert.True(Near(at(640, 512), Red, 12), $"centre {Show(at(640, 512))}");
        Assert.True(Near(at(3, 155), Red, 12), $"just inside the top edge {Show(at(3, 155))}");
        Assert.True(Near(at(1276, 868), Red, 12), $"just inside the bottom edge {Show(at(1276, 868))}");
        Assert.Equal(Black, at(640, 100));
        Assert.Equal(Black, at(640, 148));
        Assert.Equal(Black, at(640, 875));
        Assert.Equal(Black, at(640, 1020));
    }

    [Fact(Skip = NeedsGpu)]
    public void ATallPictureOnAWideScreen_HasBlackBarsAtTheSides()
    {
        using var gpu = GpuDevice.Create();
        using var converter = new GpuFrameConverter(gpu.Device);
        using var blitter = new PictureBlitter(gpu.Device);
        using var source = Solid(gpu.Device, 1280, 1024, Green);
        var picture = converter.Convert(source, 1280, 1024, 1280, 1024);
        using var screen = RenderTarget(gpu.Device, 1920, 1080);

        blitter.Draw(picture, 1280, 1024, screen, new PixelSize(1920, 1080));
        var at = Read(gpu.Device, screen, 1920, 1080);

        // FitRect says (285, 0, 1350, 1080).
        Assert.True(Near(at(960, 540), Green, 12), $"centre {Show(at(960, 540))}");
        Assert.True(Near(at(290, 540), Green, 12), $"just inside the left edge {Show(at(290, 540))}");
        Assert.Equal(Black, at(100, 540));
        Assert.Equal(Black, at(1850, 540));
    }

    /// <summary>
    /// The decoder's texture is its coded size (a multiple of 16) and the picture is its top-left corner. The
    /// rows below the picture here are green; if the source were the whole texture, a green band would show.
    /// </summary>
    [Fact(Skip = NeedsGpu)]
    public void OnlyThePictureIsDrawn_NotTheRowsOfTheTextureBelowIt()
    {
        using var gpu = GpuDevice.Create();
        using var converter = new GpuFrameConverter(gpu.Device);
        using var blitter = new PictureBlitter(gpu.Device);
        using var source = Rows(gpu.Device, 1280, 736, y => y < 720 ? Red : Green);
        var codedPicture = converter.Convert(source, 1280, 736, 1280, 736);
        using var screen = RenderTarget(gpu.Device, 1280, 720);

        blitter.Draw(codedPicture, 1280, 720, screen, new PixelSize(1280, 720));
        var at = Read(gpu.Device, screen, 1280, 720);

        foreach (var y in new[] { 0, 360, 700, 715, 719 })
        {
            Assert.True(Near(at(640, y), Red, 12), $"row {y}: {Show(at(640, y))}");
        }

        for (var y = 0; y < 720; y++)
        {
            Assert.True(at(640, y).Green < 100, $"row {y} is greenish: {Show(at(640, y))}");
        }
    }

    [Theory(Skip = NeedsGpu)]
    [InlineData(0, 0, 0)]
    [InlineData(255, 255, 255)]
    public void BlackStaysBlack_AndWhiteStaysWhite(byte blue, byte green, byte red)
    {
        using var gpu = GpuDevice.Create();
        using var converter = new GpuFrameConverter(gpu.Device);
        using var blitter = new PictureBlitter(gpu.Device);
        var colour = new Pixel(blue, green, red);
        using var source = Solid(gpu.Device, 1280, 720, colour);
        var picture = converter.Convert(source, 1280, 720, 1280, 720);
        using var screen = RenderTarget(gpu.Device, 1280, 720);

        blitter.Draw(picture, 1280, 720, screen, new PixelSize(1280, 720));
        var centre = Read(gpu.Device, screen, 1280, 720)(640, 360);

        output.WriteLine($"expected {Show(colour)}, drawn {Show(centre)}");
        Assert.True(Near(centre, colour, 3), $"drawn {Show(centre)}");
    }

    [Fact(Skip = NeedsGpu)]
    public void ThePictureSizeAndTheScreenSizeMayChangeBetweenDraws()
    {
        using var gpu = GpuDevice.Create();
        using var converter = new GpuFrameConverter(gpu.Device);
        using var blitter = new PictureBlitter(gpu.Device);
        using var screen = RenderTarget(gpu.Device, 1280, 1024);

        using (var first = Solid(gpu.Device, 1920, 1080, Red))
        {
            blitter.Draw(converter.Convert(first, 1920, 1080, 1920, 1080), 1920, 1080, screen, new PixelSize(1280, 1024));
        }

        using var second = Solid(gpu.Device, 1024, 768, Green);
        blitter.Draw(converter.Convert(second, 1024, 768, 1024, 768), 1024, 768, screen, new PixelSize(1280, 1024));
        var at = Read(gpu.Device, screen, 1280, 1024);

        // 4:3 on 5:4 fills the width and leaves a little black above and below; the earlier picture is gone.
        Assert.True(Near(at(640, 512), Green, 12), $"centre {Show(at(640, 512))}");
        Assert.Equal(Black, at(640, 2));
    }
}
