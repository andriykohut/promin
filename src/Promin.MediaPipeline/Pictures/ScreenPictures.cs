using System.Runtime.InteropServices;
using Promin.MediaPipeline.Capture;
using Promin.MediaPipeline.Scaling;
using Promin.Protocol.Video;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Promin.MediaPipeline.Pictures;

public enum PictureOutcome
{
    /// <summary>The screen changed; here is its picture.</summary>
    Picture,

    /// <summary>Nothing changed within the timeout.</summary>
    Unchanged,

    /// <summary>The screen cannot be captured right now (locked, a sign-in or UAC prompt, a mode change).</summary>
    Unavailable,
}

/// <summary>The agent PC's screen as small JPEG pictures, for the console's wall.</summary>
public interface IScreenPictures : IDisposable
{
    /// <summary>Waits up to <paramref name="timeout"/> for the screen to change. <paramref name="jpeg"/> is empty unless the outcome is <see cref="PictureOutcome.Picture"/>.</summary>
    PictureOutcome TryTake(TimeSpan timeout, out byte[] jpeg);
}

/// <summary>
/// <see cref="IScreenPictures"/> over the primary display: Desktop Duplication, a GPU shrink to
/// <see cref="PictureSizing.Fit"/>, a copy to the CPU, and <see cref="JpegPicture"/>. Not thread-safe: one caller.
/// Only one duplication of a display can exist in a process, so dispose this before making the next.
/// </summary>
public sealed class ScreenPictures : IScreenPictures
{
    private readonly DxgiScreenCapture _capture;
    private readonly GpuFrameConverter _shrink;

    // The device's own immediate context, taken once; it belongs to the device and is released with it.
    private readonly ID3D11DeviceContext _context;
    private ID3D11Texture2D? _staging;
    private PixelSize _stagingSize;

    /// <exception cref="Exception">The capture or the video processor could not be made.</exception>
    public ScreenPictures()
    {
        _capture = new DxgiScreenCapture();
        try
        {
            _context = _capture.Device.ImmediateContext;
            _shrink = new GpuFrameConverter(_capture.Device, Format.B8G8R8A8_UNorm);
        }
        catch
        {
            _capture.Dispose();
            throw;
        }
    }

    public PictureOutcome TryTake(TimeSpan timeout, out byte[] jpeg)
    {
        jpeg = [];
        if (!_capture.TryGetNextFrame(timeout, out var frame))
            return _capture.IsDuplicating ? PictureOutcome.Unchanged : PictureOutcome.Unavailable;

        var size = PictureSizing.Fit(new PixelSize(frame.Width, frame.Height));
        var small = _shrink.Convert(frame.Texture, frame.Width, frame.Height, size.Width, size.Height);
        jpeg = JpegPicture.Encode(ReadBack(small, size), size.Width, size.Height);
        return PictureOutcome.Picture;
    }

    public void Dispose()
    {
        _staging?.Dispose();
        _shrink.Dispose();
        _capture.Dispose();
    }

    private byte[] ReadBack(ID3D11Texture2D texture, PixelSize size)
    {
        if (_staging is null || _stagingSize != size)
        {
            _staging?.Dispose();
            _staging = _capture.Device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)size.Width,
                Height = (uint)size.Height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Staging,
                BindFlags = BindFlags.None,
                CPUAccessFlags = CpuAccessFlags.Read,
                MiscFlags = ResourceOptionFlags.None,
            });
            _stagingSize = size;
        }

        _context.CopyResource(_staging, texture);
        var mapped = _context.Map(_staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            var rowBytes = size.Width * 4;
            var bgra = new byte[rowBytes * size.Height];
            for (var row = 0; row < size.Height; row++)
                Marshal.Copy(mapped.DataPointer + row * (int)mapped.RowPitch, bgra, row * rowBytes, rowBytes);
            return bgra;
        }
        finally
        {
            _context.Unmap(_staging, 0);
        }
    }
}
