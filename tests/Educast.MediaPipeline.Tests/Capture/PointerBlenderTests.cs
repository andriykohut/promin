using Educast.MediaPipeline.Capture;

namespace Educast.MediaPipeline.Tests.Capture;

public class PointerBlenderTests
{
    private static byte[] Blend1x1(PointerShape shape, params byte[] destination)
    {
        var bgra = (byte[])destination.Clone();
        PointerBlender.Blend(bgra, stride: 4, regionWidth: 1, regionHeight: 1, shape, offsetX: 0, offsetY: 0);
        return bgra;
    }

    private static PointerShape ColorShape(PointerShapeKind kind, byte b, byte g, byte r, byte a) =>
        new(kind, Width: 1, Height: 1, Stride: 4, Pixels: [b, g, r, a]);

    // A 1x1 monochrome pointer: two rows of one byte each, AND mask then XOR mask,
    // the pixel being the most significant bit of each byte.
    private static PointerShape MonoShape(int andBit, int xorBit) =>
        new(PointerShapeKind.Monochrome, Width: 1, Height: 1, Stride: 1,
            Pixels: [(byte)(andBit << 7), (byte)(xorBit << 7)]);

    [Fact]
    public void Color_Opaque_ReplacesThePixel()
    {
        var result = Blend1x1(ColorShape(PointerShapeKind.Color, 10, 20, 30, 255), 200, 200, 200, 255);

        Assert.Equal(new byte[] { 10, 20, 30, 255 }, result);
    }

    [Fact]
    public void Color_Transparent_LeavesThePixel()
    {
        var result = Blend1x1(ColorShape(PointerShapeKind.Color, 10, 20, 30, 0), 200, 200, 200, 255);

        Assert.Equal(new byte[] { 200, 200, 200, 255 }, result);
    }

    [Fact]
    public void Color_HalfAlpha_Mixes()
    {
        var result = Blend1x1(ColorShape(PointerShapeKind.Color, 0, 0, 0, 128), 200, 200, 200, 255);

        Assert.InRange(result[0], 99, 101);
        Assert.InRange(result[1], 99, 101);
        Assert.InRange(result[2], 99, 101);
        Assert.Equal(255, result[3]);
    }

    [Fact]
    public void MaskedColor_Alpha00_Replaces()
    {
        var result = Blend1x1(ColorShape(PointerShapeKind.MaskedColor, 10, 20, 30, 0x00), 200, 200, 200, 255);

        Assert.Equal(new byte[] { 10, 20, 30, 255 }, result);
    }

    [Fact]
    public void MaskedColor_AlphaFF_Xors()
    {
        var result = Blend1x1(ColorShape(PointerShapeKind.MaskedColor, 0xFF, 0xFF, 0xFF, 0xFF), 200, 100, 0, 255);

        Assert.Equal(new byte[] { 55, 155, 255, 255 }, result);
    }

    [Fact]
    public void Monochrome_And0Xor0_IsBlack()
    {
        Assert.Equal(new byte[] { 0, 0, 0, 255 }, Blend1x1(MonoShape(0, 0), 200, 200, 200, 255));
    }

    [Fact]
    public void Monochrome_And0Xor1_IsWhite()
    {
        Assert.Equal(new byte[] { 255, 255, 255, 255 }, Blend1x1(MonoShape(0, 1), 200, 200, 200, 255));
    }

    [Fact]
    public void Monochrome_And1Xor0_IsUnchanged()
    {
        Assert.Equal(new byte[] { 200, 100, 0, 255 }, Blend1x1(MonoShape(1, 0), 200, 100, 0, 255));
    }

    [Fact]
    public void Monochrome_And1Xor1_Inverts()
    {
        Assert.Equal(new byte[] { 55, 155, 255, 255 }, Blend1x1(MonoShape(1, 1), 200, 100, 0, 255));
    }

    [Fact]
    public void Shape_PartlyOffTheLeftEdge_IsClipped()
    {
        var shape = new PointerShape(PointerShapeKind.Color, Width: 2, Height: 1, Stride: 8,
            Pixels: [1, 2, 3, 255, 1, 2, 3, 255]);
        byte[] bgra = [200, 200, 200, 255, 200, 200, 200, 255];

        PointerBlender.Blend(bgra, stride: 8, regionWidth: 2, regionHeight: 1, shape, offsetX: -1, offsetY: 0);

        Assert.Equal(new byte[] { 1, 2, 3, 255, 200, 200, 200, 255 }, bgra);
    }

    [Fact]
    public void Shape_WhollyOutside_ChangesNothing()
    {
        var shape = ColorShape(PointerShapeKind.Color, 1, 2, 3, 255);
        byte[] bgra = [200, 200, 200, 255, 200, 200, 200, 255];

        PointerBlender.Blend(bgra, stride: 8, regionWidth: 2, regionHeight: 1, shape, offsetX: 5, offsetY: 0);

        Assert.Equal(new byte[] { 200, 200, 200, 255, 200, 200, 200, 255 }, bgra);
    }

    // Beyond the brief's table: the multi-pixel monochrome path (bit order across a
    // byte, second mask row) and vertical clipping, which the 1x1 cases cannot reach.
    [Fact]
    public void Monochrome_EightWide_ReadsEachBitOfTheRow()
    {
        // 8x1 pointer: AND row 0b01111111 (pixel 0 is 0, the rest 1), XOR row 0b10000000.
        // Pixel 0: AND 0 XOR 1 -> white. Pixels 1..7: AND 1 XOR 0 -> unchanged.
        var shape = new PointerShape(PointerShapeKind.Monochrome, Width: 8, Height: 1, Stride: 1,
            Pixels: [0b0111_1111, 0b1000_0000]);
        var bgra = new byte[8 * 4];
        Array.Fill(bgra, (byte)100);

        PointerBlender.Blend(bgra, stride: 32, regionWidth: 8, regionHeight: 1, shape, 0, 0);

        Assert.Equal(new byte[] { 255, 255, 255, 255 }, bgra[..4]);
        for (var i = 1; i < 8; i++)
            Assert.Equal(new byte[] { 100, 100, 100, 255 }, bgra[(i * 4)..(i * 4 + 4)]);
    }

    [Fact]
    public void Shape_PartlyOffTheTop_IsClippedAndKeepsTheRightSourceRow()
    {
        // 1x2 pointer, rows opaque red then opaque blue; offsetY -1 -> only the blue row lands on row 0.
        var shape = new PointerShape(PointerShapeKind.Color, Width: 1, Height: 2, Stride: 4,
            Pixels: [0, 0, 255, 255, 255, 0, 0, 255]);
        byte[] bgra = [200, 200, 200, 255, 200, 200, 200, 255];

        PointerBlender.Blend(bgra, stride: 4, regionWidth: 1, regionHeight: 2, shape, offsetX: 0, offsetY: -1);

        Assert.Equal(new byte[] { 255, 0, 0, 255, 200, 200, 200, 255 }, bgra);
    }
}
