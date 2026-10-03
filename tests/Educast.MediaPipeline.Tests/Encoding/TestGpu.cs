using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Educast.MediaPipeline.Tests.Encoding;

/// <summary>
/// A Direct3D 11 device for the hardware-gated tests that use synthetic pictures: built the way the screen
/// capture builds its own (video support, multithread protection, on the adapter that drives the primary
/// display), so the encoder meets the same kind of device. Nothing here touches the screen.
/// </summary>
internal sealed class TestGpu : IDisposable
{
    private readonly IDXGIAdapter1 _adapter;

    public ID3D11Device Device { get; }

    public ID3D11DeviceContext Context => Device.ImmediateContext;

    public TestGpu()
    {
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        _adapter = FindAdapterOfPrimaryOutput(factory)
            ?? throw new InvalidOperationException("No adapter drives the primary display.");

        var hr = D3D11.D3D11CreateDevice(
            _adapter,
            DriverType.Unknown,
            DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport,
            [FeatureLevel.Level_11_0],
            out var device,
            out _,
            out var context);
        hr.CheckError();
        context.Dispose(); // the device keeps its own immediate context; this extra reference is not needed
        Device = device;

        using var multithread = Device.QueryInterface<ID3D11Multithread>();
        multithread.SetMultithreadProtected(true);
    }

    public string AdapterName => _adapter.Description1.Description;

    public void Dispose()
    {
        Device.Dispose();
        _adapter.Dispose();
    }

    /// <summary>A BGRA texture filled with a single colour.</summary>
    public ID3D11Texture2D SolidTexture(int width, int height, byte blue, byte green, byte red)
    {
        var pixels = new byte[width * height * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = blue;
            pixels[i + 1] = green;
            pixels[i + 2] = red;
            pixels[i + 3] = 255;
        }
        return Upload(width, height, pixels);
    }

    /// <summary>
    /// A picture that differs from one <paramref name="index"/> to the next: a gradient with a bright
    /// square that moves along the top and stripes that shift, so there is something to encode.
    /// </summary>
    public ID3D11Texture2D MovingPattern(int width, int height, int index)
    {
        var pixels = new byte[width * height * 4];
        var square = (index * 24) % Math.Max(1, width - 64);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var o = (y * width + x) * 4;
                var inSquare = y < 64 && x >= square && x < square + 64;
                var stripe = ((x + y + index * 8) / 16) % 2 == 0;
                pixels[o] = (byte)(inSquare ? 255 : x * 255 / width);
                pixels[o + 1] = (byte)(inSquare ? 255 : y * 255 / height);
                pixels[o + 2] = (byte)(inSquare ? 255 : stripe ? 200 : 40);
                pixels[o + 3] = 255;
            }
        }
        return Upload(width, height, pixels);
    }

    private ID3D11Texture2D Upload(int width, int height, byte[] pixels)
    {
        var texture = Device.CreateTexture2D(new Texture2DDescription
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

        var pin = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            var box = new Vortice.Mathematics.Box(0, 0, 0, width, height, 1);
            Context.UpdateSubresource(texture, 0, box, pin.AddrOfPinnedObject(), (uint)(width * 4), 0);
        }
        finally
        {
            pin.Free();
        }
        return texture;
    }

    /// <summary>Reads an NV12 texture back: the luma plane, then the interleaved chroma plane, without padding.</summary>
    public byte[] ReadNv12(ID3D11Texture2D nv12, int width, int height)
    {
        using var staging = Device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.NV12,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read,
            MiscFlags = ResourceOptionFlags.None,
        });
        Context.CopyResource(staging, nv12);

        var mapped = Context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            var pitch = (int)mapped.RowPitch;
            var rows = height + height / 2;
            var all = new byte[pitch * rows];
            Marshal.Copy(mapped.DataPointer, all, 0, all.Length);

            var packed = new byte[width * rows];
            for (var row = 0; row < rows; row++)
                Buffer.BlockCopy(all, row * pitch, packed, row * width, width);
            return packed;
        }
        finally
        {
            Context.Unmap(staging, 0);
        }
    }

    private static IDXGIAdapter1? FindAdapterOfPrimaryOutput(IDXGIFactory1 factory)
    {
        for (uint a = 0; factory.EnumAdapters1(a, out var adapter).Success; a++)
        {
            for (uint o = 0; adapter.EnumOutputs(o, out var output).Success; o++)
            {
                var description = output.Description;
                output.Dispose();
                if (description.AttachedToDesktop && description.DesktopCoordinates.Left == 0 && description.DesktopCoordinates.Top == 0)
                    return adapter;
            }
            adapter.Dispose();
        }
        return null;
    }
}
