namespace Educast.Protocol.Video;

public readonly record struct PixelSize(int Width, int Height);

public readonly record struct PixelRect(int X, int Y, int Width, int Height);

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

    /// <summary>
    /// Where a picture goes on a screen of a different size: scaled up or down to the
    /// largest size of the same shape that fits, centred. The rest of the screen is black.
    /// </summary>
    public static PixelRect FitRect(PixelSize picture, PixelSize screen)
    {
        ThrowIfNotPositive(picture, nameof(picture));
        ThrowIfNotPositive(screen, nameof(screen));

        long width, height;
        if ((long)picture.Width * screen.Height >= (long)picture.Height * screen.Width)
        {
            width = screen.Width;
            height = Math.Max(1, (long)picture.Height * screen.Width / picture.Width);
        }
        else
        {
            height = screen.Height;
            width = Math.Max(1, (long)picture.Width * screen.Height / picture.Height);
        }

        return new PixelRect(
            (int)((screen.Width - width) / 2), (int)((screen.Height - height) / 2), (int)width, (int)height);
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
