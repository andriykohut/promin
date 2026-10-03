using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Educast.MediaPipeline.Capture;

/// <summary>
/// Captures the primary display with DXGI Desktop Duplication and draws the mouse pointer in
/// (Desktop Duplication reports the pointer separately and never paints it into the image).
/// Not thread-safe: use it from one thread at a time.
/// </summary>
public sealed class DxgiScreenCapture : IScreenCapture
{
    private const uint InfiniteWait = 0xFFFFFFFF;

    // How long a call that cannot get a duplication right now (lock screen, UAC) waits before
    // reporting "no frame", so a caller looping on a short timeout does not spin.
    private static readonly TimeSpan RetryPause = TimeSpan.FromMilliseconds(100);

    private readonly IDXGIAdapter1 _adapter;
    private readonly ID3D11DeviceContext _context;
    private IDXGIOutputDuplication? _duplication;

    // The last desktop image, without the pointer. Kept so a pointer-only update can redraw it.
    private ID3D11Texture2D? _desktop;

    // What callers get: _desktop plus the pointer. Rewritten on every successful call.
    private ID3D11Texture2D? _frame;
    private int _width;
    private int _height;

    // Small CPU-readable texture the pixels under the pointer are copied into for blending.
    private ID3D11Texture2D? _staging;
    private int _stagingWidth;
    private int _stagingHeight;
    private byte[] _regionBuffer = [];

    private PointerShape? _shape;
    private bool _pointerVisible;
    private int _pointerX;
    private int _pointerY;

    private bool _disposed;

    /// <summary>
    /// The D3D11 device every texture this capture hands out lives on. The encoder must be built on
    /// this same device: a texture cannot be passed between two devices.
    /// </summary>
    public ID3D11Device Device { get; }

    /// <summary>Starts duplicating the primary display, the output whose desktop rectangle starts at (0, 0).</summary>
    public DxgiScreenCapture()
    {
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        _adapter = FindAdapterOfPrimaryOutput(factory)
            ?? throw new InvalidOperationException("No display output is attached at the desktop origin (0, 0); there is no primary display to capture.");

        // The adapter must be the one that drives the primary display, or DuplicateOutput fails.
        // VideoSupport and multithread protection are for the H.264 encoder that will share this device.
        var hr = D3D11.D3D11CreateDevice(
            _adapter,
            DriverType.Unknown,
            DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport,
            [FeatureLevel.Level_11_0],
            out var device,
            out _,
            out var context);
        hr.CheckError();
        Device = device!;
        _context = context!;

        using (var multithread = Device.QueryInterface<ID3D11Multithread>())
            multithread.SetMultithreadProtected(true);

        try
        {
            _duplication = DuplicatePrimaryOutput();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public bool TryGetNextFrame(TimeSpan timeout, out CapturedFrame frame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        frame = default;
        var waitMs = ToMilliseconds(timeout);

        if (_duplication is null && !TryRecreateDuplication())
        {
            Thread.Sleep(timeout < RetryPause ? timeout : RetryPause);
            return false;
        }

        var result = _duplication!.AcquireNextFrame(waitMs, out var info, out var resource);
        switch (ClassifyAcquire(result))
        {
            case AcquireOutcome.Timeout:
                return false;

            case AcquireOutcome.AccessLost:
                // Mode change, lock screen, UAC prompt, another duplication took over: the old
                // duplication is dead. Rebuild it (the next call tries again if that is not possible yet).
                resource?.Dispose();
                DropDuplication();
                TryRecreateDuplication();
                return false;

            case AcquireOutcome.Failed:
                resource?.Dispose();
                result.CheckError();
                return false;
        }

        try
        {
            var desktopChanged = info.LastPresentTime != 0;
            var pointerChanged = info.LastMouseUpdateTime != 0;
            if (!desktopChanged && !pointerChanged)
                return false;

            // The shape must be read before ReleaseFrame. A new shape is only reported when the
            // pointer's appearance changes; otherwise the last one stays in force.
            if (info.PointerShapeBufferSize > 0)
                _shape = ReadPointerShape(info.PointerShapeBufferSize);
            if (pointerChanged)
            {
                _pointerVisible = info.PointerPosition.Visible;
                _pointerX = info.PointerPosition.Position.X;
                _pointerY = info.PointerPosition.Position.Y;
            }

            if (desktopChanged)
                CopyDesktop(resource!);
            else if (_desktop is null)
                return false; // a pointer-only update before we ever saw the desktop: nothing to redraw
        }
        finally
        {
            resource?.Dispose();
            // The duplicated texture dies here, which is why we copied it into our own first.
            _duplication.ReleaseFrame();
        }

        _context.CopyResource(_frame!, _desktop!);
        if (_pointerVisible && _shape is not null)
            DrawPointer(_shape);

        frame = new CapturedFrame(_frame!, _width, _height);
        return true;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        _staging?.Dispose();
        _frame?.Dispose();
        _desktop?.Dispose();
        _duplication?.Dispose();
        _context?.Dispose();
        Device?.Dispose();
        _adapter?.Dispose();
    }

    internal enum AcquireOutcome
    {
        Frame,
        Timeout,
        AccessLost,
        Failed,
    }

    internal static AcquireOutcome ClassifyAcquire(SharpGen.Runtime.Result result)
    {
        if (result.Success)
            return AcquireOutcome.Frame;
        if (result == Vortice.DXGI.ResultCode.WaitTimeout)
            return AcquireOutcome.Timeout;
        if (result == Vortice.DXGI.ResultCode.AccessLost)
            return AcquireOutcome.AccessLost;
        return AcquireOutcome.Failed;
    }

    /// <summary>
    /// Where the part of a pointer that lies on the display goes: the rectangle on the display
    /// (<c>X</c>, <c>Y</c>, <c>Width</c>, <c>Height</c>) and where that rectangle's top-left sits
    /// relative to the shape's top-left (<c>ShapeX</c>, <c>ShapeY</c>, always &lt;= 0 when expressed
    /// as the blend offset). Returns <c>false</c> when the pointer is wholly off the display.
    /// </summary>
    internal static bool TryClipPointer(
        int pointerX, int pointerY, int shapeWidth, int shapeHeight, int displayWidth, int displayHeight,
        out (int X, int Y, int Width, int Height) region)
    {
        var left = Math.Max(0, pointerX);
        var top = Math.Max(0, pointerY);
        var right = Math.Min(displayWidth, (long)pointerX + shapeWidth);
        var bottom = Math.Min(displayHeight, (long)pointerY + shapeHeight);
        if (right <= left || bottom <= top)
        {
            region = default;
            return false;
        }
        region = (left, top, (int)(right - left), (int)(bottom - top));
        return true;
    }

    /// <summary>Turns what Desktop Duplication reports about a pointer into a <see cref="PointerShape"/>.</summary>
    internal static PointerShape? ToPointerShape(uint type, int width, int height, int pitch, byte[] buffer)
    {
        // DXGI_OUTDUPL_POINTER_SHAPE_TYPE_{MONOCHROME,COLOR,MASKED_COLOR}
        return type switch
        {
            1 => new PointerShape(PointerShapeKind.Monochrome, width, height / 2, pitch, buffer), // DXGI reports twice the height
            2 => new PointerShape(PointerShapeKind.Color, width, height, pitch, buffer),
            4 => new PointerShape(PointerShapeKind.MaskedColor, width, height, pitch, buffer),
            _ => null,
        };
    }

    private static uint ToMilliseconds(TimeSpan timeout)
    {
        if (timeout == Timeout.InfiniteTimeSpan)
            return InfiniteWait;
        if (timeout < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout), "The timeout cannot be negative (use Timeout.InfiniteTimeSpan to wait forever).");
        return (uint)Math.Min(timeout.TotalMilliseconds, InfiniteWait - 1);
    }

    private static IDXGIAdapter1? FindAdapterOfPrimaryOutput(IDXGIFactory1 factory)
    {
        for (uint a = 0; factory.EnumAdapters1(a, out var adapter).Success; a++)
        {
            if (FindPrimaryOutput(adapter) is { } output)
            {
                output.Dispose();
                return adapter;
            }
            adapter.Dispose();
        }
        return null;
    }

    private static IDXGIOutput? FindPrimaryOutput(IDXGIAdapter adapter)
    {
        for (uint o = 0; adapter.EnumOutputs(o, out var output).Success; o++)
        {
            var description = output.Description;
            if (description.AttachedToDesktop
                && description.DesktopCoordinates.Left == 0
                && description.DesktopCoordinates.Top == 0)
                return output;
            output.Dispose();
        }
        return null;
    }

    private IDXGIOutputDuplication DuplicatePrimaryOutput()
    {
        using var output = FindPrimaryOutput(_adapter)
            ?? throw new InvalidOperationException("The primary display is not attached to the adapter this capture was created on.");
        using var output1 = output.QueryInterface<IDXGIOutput1>();
        return output1.DuplicateOutput(Device);
    }

    private void DropDuplication()
    {
        _duplication?.Dispose();
        _duplication = null;
    }

    // Returns false while the duplication cannot be made (lock screen or UAC showing, mode change
    // in progress); the caller carries on and tries again next call. Anything else is a real error.
    private bool TryRecreateDuplication()
    {
        try
        {
            _duplication = DuplicatePrimaryOutput();
            return true;
        }
        catch (SharpGen.Runtime.SharpGenException ex) when (IsTransient(ex.ResultCode))
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            // The primary output is momentarily absent from the adapter during a mode change.
            return false;
        }
    }

    private static bool IsTransient(SharpGen.Runtime.Result result) =>
        result == Vortice.DXGI.ResultCode.AccessLost
        || result == Vortice.DXGI.ResultCode.AccessDenied
        || result == Vortice.DXGI.ResultCode.NotCurrentlyAvailable
        || result == Vortice.DXGI.ResultCode.ModeChangeInProgress
        || result == SharpGen.Runtime.Result.AccessDenied;

    private PointerShape? ReadPointerShape(uint bufferSize)
    {
        var buffer = new byte[bufferSize];
        var pin = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            _duplication!.GetFramePointerShape(bufferSize, pin.AddrOfPinnedObject(), out _, out var shapeInfo).CheckError();
            return ToPointerShape((uint)shapeInfo.Type, (int)shapeInfo.Width, (int)shapeInfo.Height, (int)shapeInfo.Pitch, buffer);
        }
        finally
        {
            pin.Free();
        }
    }

    private void CopyDesktop(IDXGIResource resource)
    {
        using var source = resource.QueryInterface<ID3D11Texture2D>();
        var description = source.Description;
        if (description.Format != Format.B8G8R8A8_UNorm)
            throw new NotSupportedException($"The desktop image is {description.Format}; only B8G8R8A8_UNorm is supported.");

        if (_desktop is null || _width != description.Width || _height != description.Height)
        {
            // First frame, or the display changed size: replace the textures we own.
            _frame?.Dispose();
            _desktop?.Dispose();
            _width = (int)description.Width;
            _height = (int)description.Height;
            _desktop = CreateOwnTexture(_width, _height);
            _frame = CreateOwnTexture(_width, _height);
        }

        _context.CopyResource(_desktop, source);
    }

    // BGRA, GPU-only, no CPU access flags: the encoder's colour conversion can read it and, since
    // it is on Device, so can anything else built on that device.
    private ID3D11Texture2D CreateOwnTexture(int width, int height) =>
        Device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.None,
        });

    private void DrawPointer(PointerShape shape)
    {
        if (!TryClipPointer(_pointerX, _pointerY, shape.Width, shape.Height, _width, _height, out var region))
            return;

        EnsureStaging(region.Width, region.Height);
        var box = new Vortice.Mathematics.Box(region.X, region.Y, 0, region.X + region.Width, region.Y + region.Height, 1);
        _context.CopySubresourceRegion(_staging!, 0, 0, 0, 0, _frame!, 0, box);

        // Pull the pixels under the pointer to the CPU as one tight BGRA block, blend, push them back.
        var stride = region.Width * 4;
        var length = stride * region.Height;
        if (_regionBuffer.Length < length)
            _regionBuffer = new byte[length];

        var mapped = _context.Map(_staging!, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            for (var y = 0; y < region.Height; y++)
                Marshal.Copy(mapped.DataPointer + y * (int)mapped.RowPitch, _regionBuffer, y * stride, stride);
        }
        finally
        {
            _context.Unmap(_staging!, 0);
        }

        PointerBlender.Blend(
            _regionBuffer.AsSpan(0, length), stride, region.Width, region.Height,
            shape, _pointerX - region.X, _pointerY - region.Y);

        var pin = GCHandle.Alloc(_regionBuffer, GCHandleType.Pinned);
        try
        {
            _context.UpdateSubresource(_frame!, 0, box, pin.AddrOfPinnedObject(), (uint)stride, 0);
        }
        finally
        {
            pin.Free();
        }
    }

    private void EnsureStaging(int width, int height)
    {
        if (_staging is not null && _stagingWidth >= width && _stagingHeight >= height)
            return;

        _staging?.Dispose();
        _stagingWidth = Math.Max(width, _stagingWidth);
        _stagingHeight = Math.Max(height, _stagingHeight);
        _staging = Device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)_stagingWidth,
            Height = (uint)_stagingHeight,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read,
            MiscFlags = ResourceOptionFlags.None,
        });
    }
}
