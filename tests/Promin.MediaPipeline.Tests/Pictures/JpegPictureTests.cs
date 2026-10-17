using Promin.MediaPipeline.Pictures;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Promin.MediaPipeline.Tests.Pictures;

// Windows' imaging runs without a GPU or a desktop, so these run everywhere.
public class JpegPictureTests
{
    private static byte[] Solid(int width, int height, byte blue, byte green, byte red)
    {
        var bgra = new byte[width * height * 4];
        for (var i = 0; i < bgra.Length; i += 4)
        {
            bgra[i] = blue;
            bgra[i + 1] = green;
            bgra[i + 2] = red;
            bgra[i + 3] = 255;
        }

        return bgra;
    }

    [Fact]
    public async Task Encode_MakesAJpegOfTheGivenSize_ThatDecodesToTheSameColour()
    {
        var jpeg = JpegPicture.Encode(Solid(480, 270, blue: 200, green: 100, red: 50), 480, 270);

        Assert.True(jpeg.Length > 2 && jpeg[0] == 0xFF && jpeg[1] == 0xD8, "not a JPEG");
        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
        {
            writer.WriteBytes(jpeg);
            await writer.StoreAsync();
            await writer.FlushAsync();
        }

        stream.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(stream);
        Assert.Equal(480u, decoder.PixelWidth);
        Assert.Equal(270u, decoder.PixelHeight);
        var pixels = (await decoder.GetPixelDataAsync(
            BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, new BitmapTransform(),
            ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage)).DetachPixelData();
        var middle = (135 * 480 + 240) * 4;
        Assert.InRange(pixels[middle], 200 - 6, 200 + 6);
        Assert.InRange(pixels[middle + 1], 100 - 6, 100 + 6);
        Assert.InRange(pixels[middle + 2], 50 - 6, 50 + 6);
    }

    [Fact]
    public void Encode_RefusesABufferOfTheWrongLength() =>
        Assert.Throws<ArgumentException>(() => JpegPicture.Encode(new byte[10], 480, 270));
}
