using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Promin.MediaPipeline.Pictures;

/// <summary>
/// A screen picture as a JPEG, with Windows' own imaging (WinRT, part of Windows: no package). Synchronous: it runs
/// on the agent's picture loop, never on a UI thread.
/// </summary>
public static class JpegPicture
{
    /// <summary>Enough for a glance at a tile; a 480×270 desktop comes out at roughly 20–40 KB.</summary>
    public const float Quality = 0.75f;

    /// <param name="bgra">Tight rows: <paramref name="width"/> × 4 bytes each, <paramref name="height"/> rows.</param>
    public static byte[] Encode(byte[] bgra, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(bgra);
        if (width <= 0 || height <= 0 || bgra.Length != width * height * 4)
            throw new ArgumentException($"Expected {width}x{height} BGRA ({width * height * 4} bytes); got {bgra.Length} bytes.", nameof(bgra));

        using var stream = new InMemoryRandomAccessStream();
        var options = new BitmapPropertySet { ["ImageQuality"] = new BitmapTypedValue(Quality, Windows.Foundation.PropertyType.Single) };
        var encoder = BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, stream, options).AsTask().GetAwaiter().GetResult();
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, (uint)width, (uint)height, 96, 96, bgra);
        encoder.FlushAsync().AsTask().GetAwaiter().GetResult();

        var jpeg = new byte[stream.Size];
        using var reader = new DataReader(stream.GetInputStreamAt(0));
        reader.LoadAsync((uint)stream.Size).AsTask().GetAwaiter().GetResult();
        reader.ReadBytes(jpeg);
        return jpeg;
    }
}
