using Promin.MediaPipeline.Scaling;
using Promin.MediaPipeline.Tests.Encoding;
using Xunit.Abstractions;

namespace Promin.MediaPipeline.Tests.Scaling;

public class GpuFrameConverterTests(ITestOutputHelper output)
{
    private const string NeedsGpu =
        "Requires the GPU's video processor; uses synthetic pictures and captures nothing; run manually on target hardware.";

    [Fact]
    public void Constructor_RefusesANullDevice() =>
        Assert.Throws<ArgumentNullException>(() => new GpuFrameConverter(null!));

    // Black must stay black on the agent PC's screen: full-range RGB in, limited-range (16..235) BT.709 out.
    // Red tells BT.709 from BT.601 apart: Y is about 63 under 709 and 81 under 601.
    [Theory(Skip = NeedsGpu)]
    [InlineData(0, 0, 0, 16, 128, 128)]
    [InlineData(255, 255, 255, 235, 128, 128)]
    [InlineData(255, 0, 0, 63, 102, 240)]
    [InlineData(0, 0, 255, 32, 240, 118)]
    public void SolidColours_ConvertToLimitedRangeBt709(byte red, byte green, byte blue, int y, int cb, int cr)
    {
        using var gpu = new TestGpu();
        using var converter = new GpuFrameConverter(gpu.Device);
        using var source = gpu.SolidTexture(64, 64, blue, green, red);

        var nv12 = converter.Convert(source, 64, 64, 64, 64);
        var bytes = gpu.ReadNv12(nv12, 64, 64);

        // Middle of the picture: luma at (32, 32), chroma pair at chroma row 16, column 16.
        var luma = bytes[32 * 64 + 32];
        var chromaRow = 64 + 16;
        var cbValue = bytes[chromaRow * 64 + 32];
        var crValue = bytes[chromaRow * 64 + 33];
        output.WriteLine($"rgb({red},{green},{blue}) -> Y={luma} Cb={cbValue} Cr={crValue}; expected about Y={y} Cb={cb} Cr={cr}");

        Assert.InRange(luma, y - 3, y + 3);
        Assert.InRange(cbValue, cb - 4, cb + 4);
        Assert.InRange(crValue, cr - 4, cr + 4);
    }

    [Fact(Skip = NeedsGpu)]
    public void Scaling_KeepsAWhitePictureWhite_AndProducesATextureOfTheTargetSize()
    {
        using var gpu = new TestGpu();
        using var converter = new GpuFrameConverter(gpu.Device);
        using var source = gpu.SolidTexture(128, 96, 255, 255, 255);

        var nv12 = converter.Convert(source, 128, 96, 64, 48);

        Assert.Equal(64u, nv12.Description.Width);
        Assert.Equal(48u, nv12.Description.Height);
        var bytes = gpu.ReadNv12(nv12, 64, 48);
        Assert.InRange(bytes[24 * 64 + 32], 232, 235);
        Assert.InRange(bytes[0], 232, 235); // the corner too: no border from the scaling
        Assert.InRange(bytes[64 * 48 - 1], 232, 235);
    }

    [Fact(Skip = NeedsGpu)]
    public void ChangingTheSourceOrTargetSize_RebuildsTheProcessor_AndKeepsWorking()
    {
        using var gpu = new TestGpu();
        using var converter = new GpuFrameConverter(gpu.Device);
        using var small = gpu.SolidTexture(64, 64, 255, 255, 255);
        using var large = gpu.SolidTexture(200, 100, 0, 0, 0);

        Assert.Equal(32u, converter.Convert(small, 64, 64, 32, 32).Description.Width);
        Assert.Equal(32u, converter.Convert(large, 200, 100, 32, 32).Description.Width);   // new source size
        Assert.Equal(100u, converter.Convert(large, 200, 100, 100, 50).Description.Width); // new target size
        Assert.Equal(100u, converter.Convert(small, 64, 64, 100, 50).Description.Width);   // and back to the first source
    }

    [Fact(Skip = NeedsGpu)]
    public void ConsecutiveCalls_ReturnDifferentTextures_SoAnEncoderStillReadingTheLastIsSafe()
    {
        using var gpu = new TestGpu();
        using var converter = new GpuFrameConverter(gpu.Device);
        using var source = gpu.SolidTexture(64, 64, 10, 20, 30);

        var first = converter.Convert(source, 64, 64, 64, 64);
        var second = converter.Convert(source, 64, 64, 64, 64);
        var third = converter.Convert(source, 64, 64, 64, 64);

        Assert.NotEqual(first.NativePointer, second.NativePointer);
        Assert.NotEqual(second.NativePointer, third.NativePointer);
        Assert.NotEqual(first.NativePointer, third.NativePointer);
    }

    [Fact(Skip = NeedsGpu)]
    public void Convert_RefusesWhatItCannotConvert()
    {
        using var gpu = new TestGpu();
        using var converter = new GpuFrameConverter(gpu.Device);
        using var source = gpu.SolidTexture(64, 64, 0, 0, 0);

        Assert.Throws<ArgumentOutOfRangeException>(() => converter.Convert(source, 64, 64, 33, 32));  // odd target
        Assert.Throws<ArgumentOutOfRangeException>(() => converter.Convert(source, 0, 64, 32, 32));   // empty source
        Assert.Throws<ArgumentException>(() => converter.Convert(source, 128, 64, 32, 32));            // texture smaller than claimed
    }
}
