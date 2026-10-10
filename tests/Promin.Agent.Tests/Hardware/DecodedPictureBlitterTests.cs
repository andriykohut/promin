using Promin.MediaPipeline.Capture;
using Promin.MediaPipeline.Decoding;
using Promin.MediaPipeline.Encoding;
using Promin.Protocol.Video;
using Xunit.Abstractions;
using static Promin.Agent.Tests.Hardware.GpuPictures;

namespace Promin.Agent.Tests.Hardware;

// The test the agent PC's display was missing: the decoder's OWN texture, not a converter's, handed to the
// blitter. PictureBlitterTests feed the blitter textures the capture side's converter makes (render-target
// bound); the decoder makes its texture differently, and a texture the video processor refuses as input shows
// up only in a real broadcast. This needs the GPU, the video processor and Media Foundation; it opens no window,
// shows nothing and captures nothing. Run it by hand on the target machine (remove the Skip text, run, put it
// back).
public class DecodedPictureBlitterTests(ITestOutputHelper output)
{
    private const string NeedsGpu =
        "Requires the GPU, its video processor and Media Foundation; synthetic pictures, no window, captures nothing; run manually on target hardware.";

    private static readonly Pixel Black = new(0, 0, 0);
    private static readonly Pixel Red = new(0, 0, 255);

    private static bool Near(Pixel actual, Pixel expected, int tolerance) =>
        Math.Abs(actual.Blue - expected.Blue) <= tolerance
        && Math.Abs(actual.Green - expected.Green) <= tolerance
        && Math.Abs(actual.Red - expected.Red) <= tolerance;

    private static string Show(Pixel p) => $"(B{p.Blue} G{p.Green} R{p.Red})";

    /// <summary>A red keyframe of the given size, encoded for real.</summary>
    private static EncodedFrame EncodeRedKeyframe(GpuDevice gpu, int width, int height)
    {
        using var encoder = new MediaFoundationH264Encoder(
            gpu.Device, width, height, 15, VideoBitrate.BitsPerSecond(new PixelSize(width, height), 15, Sharpness.Standard));
        using var picture = Solid(gpu.Device, width, height, Red);
        Assert.True(
            encoder.TryEncode(new CapturedFrame(picture, width, height), forceKeyframe: true, out var encoded),
            "the encoder returned no picture");
        return encoded;
    }

    [Theory(Skip = NeedsGpu)]
    [InlineData(true, 1920, 1080)]   // a coded size of 1920x1088: the picture is the top-left 1080 rows
    [InlineData(false, 1920, 1080)]
    [InlineData(true, 1024, 768)]    // a size with no padding
    [InlineData(false, 1024, 768)]
    public void TheDecodersOwnTexture_IsDrawnByTheBlitter_WholeAndCentred(bool hardwareDecoding, int width, int height)
    {
        using var gpu = GpuDevice.Create();
        output.WriteLine($"adapter: {gpu.AdapterName}; decoding in {(hardwareDecoding ? "hardware" : "software")}");
        var encoded = EncodeRedKeyframe(gpu, width, height);
        using var decoder = new MediaFoundationH264Decoder(gpu.Device, allowHardware: hardwareDecoding);

        Assert.True(decoder.TryDecode(encoded, out var frame), $"no picture: {decoder.LastFailure}");
        var d = frame.Texture.Description;
        output.WriteLine($"the decoder's texture: {d.Width}x{d.Height} {d.Format}, usage {d.Usage}, bind flags {d.BindFlags}; picture {frame.Width}x{frame.Height}; {decoder.Configuration}");

        using var blitter = new PictureBlitter(gpu.Device);
        using var screen = RenderTarget(gpu.Device, 1280, 1024);
        blitter.Draw(frame.Texture, frame.Width, frame.Height, screen, new PixelSize(1280, 1024));

        var at = Read(gpu.Device, screen, 1280, 1024);
        var fit = FrameSizing.FitRect(new PixelSize(width, height), new PixelSize(1280, 1024));
        output.WriteLine($"the picture goes to {fit}");

        // Inside, all red, right up to the edges of the rectangle (no band of undecoded rows along the bottom).
        foreach (var (x, y) in new[]
        {
            (fit.X + fit.Width / 2, fit.Y + fit.Height / 2),
            (fit.X + 3, fit.Y + 3),
            (fit.X + fit.Width - 4, fit.Y + 3),
            (fit.X + 3, fit.Y + fit.Height - 4),
            (fit.X + fit.Width - 4, fit.Y + fit.Height - 4),
        })
        {
            Assert.True(Near(at(x, y), Red, 14), $"({x},{y}) inside the picture is {Show(at(x, y))}");
        }

        // Outside, black.
        if (fit.Y > 4)
        {
            Assert.Equal(Black, at(640, fit.Y - 3));
            Assert.Equal(Black, at(640, fit.Y + fit.Height + 2));
        }

        if (fit.X > 4)
        {
            Assert.Equal(Black, at(fit.X - 3, 512));
        }
    }
}
