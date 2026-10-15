using Promin.Protocol.Video;

namespace Promin.MediaPipeline.Pictures;

/// <summary>
/// The size a screen is shrunk to for its picture on the console's wall: inside <see cref="Bounds"/>, the aspect
/// kept, never enlarged. The tiles fit the picture whole, so nothing here crops.
/// </summary>
public static class PictureSizing
{
    public static PixelSize Bounds { get; } = new(480, 300);

    public static PixelSize Fit(PixelSize screen)
    {
        if (screen.Width <= 0 || screen.Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(screen), $"A screen has a positive size; got {screen.Width}x{screen.Height}.");

        var scale = Math.Min(1.0, Math.Min((double)Bounds.Width / screen.Width, (double)Bounds.Height / screen.Height));
        return new PixelSize(
            Math.Max(1, (int)Math.Round(screen.Width * scale)),
            Math.Max(1, (int)Math.Round(screen.Height * scale)));
    }
}
