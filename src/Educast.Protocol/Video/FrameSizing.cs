namespace Educast.Protocol.Video;

public readonly record struct PixelSize(int Width, int Height);

public static class FrameSizing
{
    /// <summary>
    /// The size to encode at: the source size when it already fits inside the ceiling,
    /// otherwise the largest size of the same shape that does. Never larger than the
    /// source. Both dimensions are even, as H.264 4:2:0 requires.
    /// </summary>
    public static PixelSize EncodeSize(PixelSize source, PixelSize ceiling)
    {
        ThrowIfNotPositive(source, nameof(source));
        ThrowIfNotPositive(ceiling, nameof(ceiling));

        var width = (long)source.Width;
        var height = (long)source.Height;
        if (width > ceiling.Width || height > ceiling.Height)
        {
            // Compare the two ratios by cross-multiplying: no floating point, no rounding drift.
            if (width * ceiling.Height >= height * ceiling.Width)
            {
                height = height * ceiling.Width / width;
                width = ceiling.Width;
            }
            else
            {
                width = width * ceiling.Height / height;
                height = ceiling.Height;
            }
        }

        return new PixelSize(Even(width), Even(height));
    }

    private static int Even(long value) => (int)Math.Max(2, value & ~1L);

    private static void ThrowIfNotPositive(PixelSize size, string name)
    {
        if (size.Width <= 0 || size.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(name, size, "Width and height must be positive.");
        }
    }
}
