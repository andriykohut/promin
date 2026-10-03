using Educast.MediaPipeline.Decoding;

namespace Educast.MediaPipeline.Tests.Decoding;

// The size a decoded picture is reported with: the display aperture when the output type has one, otherwise
// the frame size. Reporting the coded size puts a band of garbage along the bottom of every student's screen.
public class OutputPictureSizeTests
{
    private static ulong Pack(int width, int height) => ((ulong)(uint)width << 32) | (uint)height;

    // MFVideoArea: MFOffset OffsetX { WORD fract; short value }, MFOffset OffsetY, SIZE Area { long cx, cy }.
    private static byte[] Area(int width, int height, short x = 0, short y = 0, ushort fractX = 0, ushort fractY = 0)
    {
        var blob = new byte[16];
        BitConverter.GetBytes(fractX).CopyTo(blob, 0);
        BitConverter.GetBytes(x).CopyTo(blob, 2);
        BitConverter.GetBytes(fractY).CopyTo(blob, 4);
        BitConverter.GetBytes(y).CopyTo(blob, 6);
        BitConverter.GetBytes(width).CopyTo(blob, 8);
        BitConverter.GetBytes(height).CopyTo(blob, 12);
        return blob;
    }

    [Fact]
    public void Unpack_SplitsAFrameSizeAttribute_WidthInTheHighHalf()
    {
        OutputPictureSize.Unpack(Pack(1920, 1088), out var width, out var height);

        Assert.Equal((1920, 1088), (width, height));
    }

    [Fact]
    public void TryParseVideoArea_ReadsTheOffsetsAndTheSize()
    {
        Assert.True(OutputPictureSize.TryParseVideoArea(Area(1920, 1080, x: 4, y: 6), out var x, out var y, out var width, out var height));

        Assert.Equal((4, 6, 1920, 1080), (x, y, width, height));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(15)]
    public void TryParseVideoArea_RefusesABlobThatIsTooShort(int length)
    {
        Assert.False(OutputPictureSize.TryParseVideoArea(new byte[length], out _, out _, out _, out _));
    }

    [Theory]
    [InlineData(1920, 1088, 1920, 1080)]
    [InlineData(1920, 816, 1920, 802)]
    [InlineData(1376, 768, 1366, 768)]
    [InlineData(608, 1088, 606, 1080)]
    public void TheDisplayAperture_IsThePictureSize_NotTheCodedSize(int codedWidth, int codedHeight, int width, int height)
    {
        Assert.True(OutputPictureSize.TryChoose(Pack(codedWidth, codedHeight), Area(width, height), defaultStride: 0, out var geometry));

        Assert.Equal((width, height), (geometry.PictureWidth, geometry.PictureHeight));
        Assert.Equal((codedWidth, codedHeight), (geometry.CodedWidth, geometry.CodedHeight));
        Assert.Equal((0, 0), (geometry.OriginX, geometry.OriginY));
    }

    [Fact]
    public void WithoutAnAperture_TheFrameSizeIsThePictureSize()
    {
        Assert.True(OutputPictureSize.TryChoose(Pack(1366, 768), apertureBlob: null, defaultStride: 0, out var geometry));

        Assert.Equal((1366, 768, 1366, 768), (geometry.CodedWidth, geometry.CodedHeight, geometry.PictureWidth, geometry.PictureHeight));
    }

    [Theory]
    [MemberData(nameof(UnusableApertures))]
    public void AnApertureThatCannotBeRight_IsIgnored_AndTheFrameSizeUsed(byte[] aperture)
    {
        Assert.True(OutputPictureSize.TryChoose(Pack(1920, 1088), aperture, defaultStride: 0, out var geometry));

        Assert.Equal((1920, 1088), (geometry.PictureWidth, geometry.PictureHeight));
    }

    public static TheoryData<byte[]> UnusableApertures() => new()
    {
        Area(0, 1080),                 // empty
        Area(1920, 0),
        Area(-1920, 1080),             // negative
        Area(2000, 1080),              // wider than the frame
        Area(1920, 1200),              // taller than the frame
        Area(1920, 1080, x: 8),        // runs off the right edge
        Area(1920, 1080, y: 16),       // runs off the bottom edge
        Area(1920, 1080, x: -2),       // negative origin
        new byte[7],                   // not a video area at all
    };

    [Fact]
    public void AnAperture_ThatDoesNotStartAtTheCorner_ReportsItsOrigin()
    {
        Assert.True(OutputPictureSize.TryChoose(Pack(1920, 1088), Area(1900, 1070, x: 8, y: 2), defaultStride: 0, out var geometry));

        Assert.Equal((8, 2, 1900, 1070), (geometry.OriginX, geometry.OriginY, geometry.PictureWidth, geometry.PictureHeight));
    }

    [Fact]
    public void TheFractionalPartOfAnOffset_IsIgnored()
    {
        Assert.True(OutputPictureSize.TryChoose(Pack(1920, 1088), Area(1920, 1080, fractX: 0x8000, fractY: 0x4000), 0, out var geometry));

        Assert.Equal((0, 0), (geometry.OriginX, geometry.OriginY));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1920, 0)]
    [InlineData(0, 1080)]
    [InlineData(100000, 1080)] // beyond anything a decoder produces
    public void ANonsenseFrameSize_IsRefused(int width, int height)
    {
        Assert.False(OutputPictureSize.TryChoose(Pack(width, height), apertureBlob: null, defaultStride: 0, out _));
    }

    [Theory]
    [InlineData(0u, 1920)]            // no stride attribute: the rows are as wide as the frame
    [InlineData(2048u, 2048)]
    [InlineData(1900u, 1920)]         // narrower than a row can be: not believed
    [InlineData(0xFFFFF800u, 1920)]   // negative (a bottom-up surface): not supported, not believed
    public void TheStride_IsTheOneTheTypeGave_WhenItCanBeRight_OtherwiseTheFrameWidth(uint stride, int expected)
    {
        Assert.True(OutputPictureSize.TryChoose(Pack(1920, 1088), apertureBlob: null, stride, out var geometry));

        Assert.Equal(expected, geometry.Stride);
    }
}
