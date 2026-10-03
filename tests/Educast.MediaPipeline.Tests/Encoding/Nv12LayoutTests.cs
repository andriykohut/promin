using Educast.MediaPipeline.Encoding;

namespace Educast.MediaPipeline.Tests.Encoding;

public class Nv12LayoutTests
{
    [Theory]
    [InlineData(2, 2, 6)]
    [InlineData(1920, 1080, 3_110_400)]
    [InlineData(606, 1080, 981_720)]
    public void Size_IsOneAndAHalfBytesPerPixel(int width, int height, int expected) =>
        Assert.Equal(expected, Nv12Layout.Size(width, height));

    [Fact]
    public void Pack_DropsTheRowPadding_OfBothPlanes()
    {
        // 4x4 picture, rows padded to 8 bytes. Y rows are 10..13; UV rows are 20 and 21.
        const int pitch = 8;
        var mapped = new byte[pitch * 6];
        Array.Fill(mapped, (byte)99); // padding everywhere, to be left behind
        for (var row = 0; row < 4; row++)
            Array.Fill(mapped, (byte)(10 + row), row * pitch, 4);
        for (var row = 0; row < 2; row++)
            Array.Fill(mapped, (byte)(20 + row), (4 + row) * pitch, 4);

        var packed = new byte[Nv12Layout.Size(4, 4)];
        Nv12Layout.Pack(mapped, pitch, 4, 4, packed);

        Assert.Equal(
            [10, 10, 10, 10, 11, 11, 11, 11, 12, 12, 12, 12, 13, 13, 13, 13, 20, 20, 20, 20, 21, 21, 21, 21],
            packed);
    }

    [Fact]
    public void Pack_WorksWhenThePitchEqualsTheWidth()
    {
        // 2x4 NV12 with no padding: 8 luma bytes, then 2 chroma rows of 2 bytes.
        byte[] mapped = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12];
        var packed = new byte[Nv12Layout.Size(2, 4)];

        Nv12Layout.Pack(mapped, 2, 2, 4, packed);

        Assert.Equal(mapped, packed);
    }

    [Fact]
    public void Pack_AcceptsAMappedBlockWhoseLastRowIsNotPadded()
    {
        // The last chroma row only needs `width` bytes, not a whole pitch.
        const int pitch = 8;
        var mapped = new byte[Nv12Layout.MinimumMappedLength(pitch, 4, 4)];
        Assert.Equal(pitch * 5 + 4, mapped.Length);

        Nv12Layout.Pack(mapped, pitch, 4, 4, new byte[Nv12Layout.Size(4, 4)]);
    }

    [Fact]
    public void Pack_RefusesAMappedBlockThatIsTooShort()
    {
        var mapped = new byte[Nv12Layout.MinimumMappedLength(8, 4, 4) - 1];

        Assert.Throws<ArgumentException>(() => Nv12Layout.Pack(mapped, 8, 4, 4, new byte[Nv12Layout.Size(4, 4)]));
    }

    [Fact]
    public void Pack_RefusesADestinationThatIsTooShort()
    {
        var mapped = new byte[Nv12Layout.MinimumMappedLength(8, 4, 4)];

        Assert.Throws<ArgumentException>(() => Nv12Layout.Pack(mapped, 8, 4, 4, new byte[Nv12Layout.Size(4, 4) - 1]));
    }

    [Theory]
    [InlineData(3, 4, 8)]  // odd width
    [InlineData(4, 3, 8)]  // odd height
    [InlineData(0, 4, 8)]
    [InlineData(4, 4, 3)]  // pitch narrower than the width
    public void Pack_RefusesAShapeNv12CannotHave(int width, int height, int pitch) =>
        Assert.Throws<ArgumentException>(() => Nv12Layout.Pack(new byte[1024], pitch, width, height, new byte[1024]));
}
