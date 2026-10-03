namespace Educast.MediaPipeline.Capture;

/// <summary>
/// Draws a <see cref="PointerShape"/> into a BGRA pixel region. Pure logic, no GPU: the capture
/// reads the pixels under the pointer back to the CPU, calls this, and writes them to the texture.
/// </summary>
public static class PointerBlender
{
    private const int BytesPerPixel = 4;

    /// <summary>
    /// Draws <paramref name="shape"/> with its top-left at (<paramref name="offsetX"/>,
    /// <paramref name="offsetY"/>) inside a <paramref name="regionWidth"/> x <paramref name="regionHeight"/>
    /// BGRA region whose rows are <paramref name="stride"/> bytes apart. Whatever falls outside the
    /// region is clipped; the offsets may be negative. The output alpha byte is always 0xFF.
    /// </summary>
    public static void Blend(
        Span<byte> bgra, int stride, int regionWidth, int regionHeight,
        PointerShape shape, int offsetX, int offsetY)
    {
        ArgumentNullException.ThrowIfNull(shape);
        ArgumentOutOfRangeException.ThrowIfNegative(regionWidth);
        ArgumentOutOfRangeException.ThrowIfNegative(regionHeight);
        if (regionWidth > 0 && regionHeight > 0)
        {
            if (stride < regionWidth * BytesPerPixel)
                throw new ArgumentOutOfRangeException(nameof(stride), "Stride is narrower than a row of the region.");
            if ((long)stride * (regionHeight - 1) + (long)regionWidth * BytesPerPixel > bgra.Length)
                throw new ArgumentException("The pixel buffer is smaller than the region it claims to hold.", nameof(bgra));
        }

        ValidateShape(shape);

        // The part of the shape that lands inside the region, in shape coordinates.
        var firstX = Math.Max(0, -offsetX);
        var firstY = Math.Max(0, -offsetY);
        var endX = (int)Math.Min(shape.Width, (long)regionWidth - offsetX);
        var endY = (int)Math.Min(shape.Height, (long)regionHeight - offsetY);

        for (var sy = firstY; sy < endY; sy++)
        {
            var rowStart = (offsetY + sy) * stride;
            for (var sx = firstX; sx < endX; sx++)
            {
                var d = rowStart + (offsetX + sx) * BytesPerPixel;
                switch (shape.Kind)
                {
                    case PointerShapeKind.Color:
                        BlendColor(bgra.Slice(d, BytesPerPixel), shape.Pixels.AsSpan(sy * shape.Stride + sx * BytesPerPixel, BytesPerPixel));
                        break;
                    case PointerShapeKind.MaskedColor:
                        BlendMasked(bgra.Slice(d, BytesPerPixel), shape.Pixels.AsSpan(sy * shape.Stride + sx * BytesPerPixel, BytesPerPixel));
                        break;
                    default:
                        BlendMonochrome(bgra.Slice(d, BytesPerPixel), shape, sx, sy);
                        break;
                }
            }
        }
    }

    private static void ValidateShape(PointerShape shape)
    {
        if (shape.Width < 0 || shape.Height < 0)
            throw new ArgumentException("Pointer shape has a negative size.", nameof(shape));
        if (shape.Width == 0 || shape.Height == 0)
            return;

        switch (shape.Kind)
        {
            case PointerShapeKind.Color:
            case PointerShapeKind.MaskedColor:
                if (shape.Stride < shape.Width * BytesPerPixel
                    || (long)shape.Stride * (shape.Height - 1) + (long)shape.Width * BytesPerPixel > shape.Pixels.Length)
                    throw new ArgumentException("Pointer shape buffer is smaller than its size and stride say.", nameof(shape));
                break;
            case PointerShapeKind.Monochrome:
                if (shape.Stride < (shape.Width + 7) / 8
                    || (long)shape.Stride * 2 * shape.Height > shape.Pixels.Length)
                    throw new ArgumentException("Monochrome pointer buffer must hold an AND and an XOR mask of Height rows each.", nameof(shape));
                break;
            default:
                throw new ArgumentException($"Unknown pointer shape kind {shape.Kind}.", nameof(shape));
        }
    }

    // out = src * a + dst * (1 - a), rounded; a = 0 and a = 255 are exact.
    private static void BlendColor(Span<byte> dst, ReadOnlySpan<byte> src)
    {
        int a = src[3];
        for (var c = 0; c < 3; c++)
            dst[c] = (byte)((src[c] * a + dst[c] * (255 - a) + 127) / 255);
        dst[3] = 0xFF;
    }

    // Alpha 0x00 replaces the destination; anything else (0xFF in practice) XORs it.
    private static void BlendMasked(Span<byte> dst, ReadOnlySpan<byte> src)
    {
        if (src[3] == 0)
        {
            dst[0] = src[0];
            dst[1] = src[1];
            dst[2] = src[2];
        }
        else
        {
            dst[0] ^= src[0];
            dst[1] ^= src[1];
            dst[2] ^= src[2];
        }
        dst[3] = 0xFF;
    }

    private static void BlendMonochrome(Span<byte> dst, PointerShape shape, int x, int y)
    {
        var shift = 7 - (x & 7);
        var and = (shape.Pixels[y * shape.Stride + (x >> 3)] >> shift) & 1;
        var xor = (shape.Pixels[(shape.Height + y) * shape.Stride + (x >> 3)] >> shift) & 1;

        // dst = (dst AND mask) XOR xor, applied to every colour byte.
        var andByte = (byte)(and == 1 ? 0xFF : 0x00);
        var xorByte = (byte)(xor == 1 ? 0xFF : 0x00);
        for (var c = 0; c < 3; c++)
            dst[c] = (byte)((dst[c] & andByte) ^ xorByte);
        dst[3] = 0xFF;
    }
}
