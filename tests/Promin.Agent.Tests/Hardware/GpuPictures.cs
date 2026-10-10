using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Promin.Agent.Tests.Hardware;

/// <summary>Synthetic pictures on the GPU, and reading the screen-sized result back. Nothing here touches the screen.</summary>
internal static class GpuPictures
{
    public readonly record struct Pixel(byte Blue, byte Green, byte Red);

    /// <summary>A BGRA texture with <paramref name="colourOf"/> deciding each row's colour.</summary>
    public static ID3D11Texture2D Rows(ID3D11Device device, int width, int height, Func<int, Pixel> colourOf)
    {
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            var colour = colourOf(y);
            for (var x = 0; x < width; x++)
            {
                var o = (y * width + x) * 4;
                pixels[o] = colour.Blue;
                pixels[o + 1] = colour.Green;
                pixels[o + 2] = colour.Red;
                pixels[o + 3] = 255;
            }
        }

        var texture = device.CreateTexture2D(new Texture2DDescription
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
            device.ImmediateContext.UpdateSubresource(texture, 0, box, pin.AddrOfPinnedObject(), (uint)(width * 4), 0);
        }
        finally
        {
            pin.Free();
        }

        return texture;
    }

    public static ID3D11Texture2D Solid(ID3D11Device device, int width, int height, Pixel colour) =>
        Rows(device, width, height, _ => colour);

    /// <summary>An empty BGRA texture to draw into, as the swap chain's back buffer is.</summary>
    public static ID3D11Texture2D RenderTarget(ID3D11Device device, int width, int height) =>
        device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.None,
        });

    /// <summary>Reads a BGRA texture back: a pixel at (x, y) afterwards is <c>Read(...)(x, y)</c>.</summary>
    public static Func<int, int, Pixel> Read(ID3D11Device device, ID3D11Texture2D texture, int width, int height)
    {
        using var staging = device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read,
            MiscFlags = ResourceOptionFlags.None,
        });
        var context = device.ImmediateContext;
        context.CopyResource(staging, texture); // a test reading its result back; the render path never copies

        var mapped = context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            var pitch = (int)mapped.RowPitch;
            var all = new byte[pitch * height];
            Marshal.Copy(mapped.DataPointer, all, 0, all.Length);
            return (x, y) =>
            {
                var o = y * pitch + x * 4;
                return new Pixel(all[o], all[o + 1], all[o + 2]);
            };
        }
        finally
        {
            context.Unmap(staging, 0);
        }
    }
}
