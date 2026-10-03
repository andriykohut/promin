using Educast.MediaPipeline.Encoding;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.MediaFoundation;

namespace Educast.MediaPipeline.Decoding;

/// <summary>
/// The decoder's own output texture: every decoded picture is copied into it (on the GPU for hardware
/// decoding, uploaded for software decoding) and that texture is what <see cref="DecodedFrame"/> carries.
/// This is what makes "valid until the next call" true: the transform's own surfaces are slices of a pool that
/// the transform reuses, and holding one back would starve it, so the decoder never lends them out. The
/// texture is as large as the transform's surface (the coded size, a multiple of 16 on most decoders); the
/// picture is its top-left corner. Not thread-safe.
/// </summary>
internal sealed class OutputSurface : IDisposable
{
    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly BindFlags _bind;
    private ID3D11Texture2D? _texture;
    private uint _width;
    private uint _height;

    internal OutputSurface(ID3D11Device device)
    {
        _device = device;
        // One wrapper for the device's immediate context, which belongs to the device and goes with it.
        _context = device.ImmediateContext;

        // Shader-resource binding is only added where the driver supports it, as for the converter's output.
        _bind = (device.CheckFormatSupport(Format.NV12) & FormatSupport.ShaderLoad) != 0 ? BindFlags.ShaderResource : BindFlags.None;
    }

    /// <summary>
    /// Copies <paramref name="picture"/> into the output texture. False, with the reason, when the picture
    /// cannot be shown (the decoder has not gone wrong, so the stream carries on). A failure of the GPU itself
    /// throws.
    /// </summary>
    internal bool TryPresent(DecodedPicture picture, out DecodedFrame frame, out string? problem)
    {
        frame = default;
        problem = null;
        var geometry = picture.Geometry;

        if (geometry.OriginX != 0 || geometry.OriginY != 0)
        {
            problem = $"the picture starts at ({geometry.OriginX},{geometry.OriginY}) in its surface, not at the corner; that is not supported";
            return false;
        }

        if (picture.Texture is { } source)
        {
            if (!TryCopyFromTexture(source, picture.SubresourceIndex, geometry, out problem))
                return false;
        }
        else
        {
            if (!TryUpload(picture, geometry, out problem))
                return false;
        }

        frame = new DecodedFrame(_texture!, geometry.PictureWidth, geometry.PictureHeight);
        return true;
    }

    private bool TryCopyFromTexture(ID3D11Texture2D source, uint subresource, PictureGeometry geometry, out string? problem)
    {
        problem = null;
        var description = source.Description;
        if (description.Format != Format.NV12)
        {
            problem = $"the decoder's texture is {description.Format}, not NV12";
            return false;
        }
        if (subresource >= (uint)description.MipLevels * description.ArraySize)
        {
            problem = $"the decoder's picture is slice {subresource} of a texture with {description.ArraySize}";
            return false;
        }
        if (description.Width < (uint)geometry.PictureWidth || description.Height < (uint)geometry.PictureHeight)
        {
            problem = $"the decoder's texture ({description.Width}x{description.Height}) is smaller than its picture ({geometry.PictureWidth}x{geometry.PictureHeight})";
            return false;
        }

        EnsureTexture(description.Width, description.Height);
        // Whole subresource: no source box. The overload is (dst, dstSub, dstX, dstY, dstZ, src, srcSub, Box? srcBox).
        Vortice.Mathematics.Box? wholeSubresource = null;
        _context.CopySubresourceRegion(_texture!, 0u, 0u, 0u, 0u, source, subresource, wholeSubresource);
        return true;
    }

    private bool TryUpload(DecodedPicture picture, PictureGeometry geometry, out string? problem)
    {
        problem = null;
        var buffer = picture.Memory!;
        var width = geometry.CodedWidth;
        var height = geometry.CodedHeight;
        if (width % 2 != 0 || height % 2 != 0)
        {
            problem = $"the decoder's surface is {width}x{height}; NV12 needs an even size";
            return false;
        }

        var length = buffer.CurrentLength;
        using var flat = buffer.QueryInterfaceOrNull<IMF2DBuffer>();
        if (flat is not null)
        {
            flat.Lock2D(out var scanline0, out var pitch);
            try
            {
                if (pitch < width)
                {
                    problem = $"the decoder's rows are {pitch} bytes apart, narrower than the {width} pixels in a row";
                    return false;
                }
                if (length < Nv12Layout.MinimumMappedLength(pitch, width, height))
                {
                    problem = $"the decoder's picture is {length} bytes, too short for {width}x{height} at a row pitch of {pitch}";
                    return false;
                }
                Upload(scanline0, pitch, width, height);
                return true;
            }
            finally
            {
                flat.Unlock2D();
            }
        }

        buffer.Lock(out var data, out _, out var currentLength);
        try
        {
            var stride = geometry.Stride;
            if (currentLength < Nv12Layout.MinimumMappedLength(stride, width, height))
            {
                problem = $"the decoder's picture is {currentLength} bytes, too short for {width}x{height} at a row pitch of {stride}";
                return false;
            }
            Upload(data, stride, width, height);
            return true;
        }
        finally
        {
            buffer.Unlock();
        }
    }

    // The chroma plane follows the luma plane at the same pitch (and the texture is as tall as the surface
    // the decoder laid the bytes out for), which is how the upload reads it.
    private void Upload(nint data, int pitch, int width, int height)
    {
        EnsureTexture((uint)width, (uint)height);
        // The overload is (dst, dstSub, Box? dstBox, IntPtr srcData, srcRowPitch, srcDepthPitch); no box: the whole texture.
        Vortice.Mathematics.Box? wholeTexture = null;
        _context.UpdateSubresource(_texture!, 0u, wholeTexture, data, (uint)pitch, 0u);
    }

    private void EnsureTexture(uint width, uint height)
    {
        if (_texture is not null && _width == width && _height == height)
            return;

        _texture?.Dispose();
        _texture = null;
        _texture = _device.CreateTexture2D(new Texture2DDescription
        {
            Width = width,
            Height = height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.NV12,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = _bind,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.None,
        });
        _width = width;
        _height = height;
    }

    public void Dispose()
    {
        _texture?.Dispose();
        _texture = null;
    }
}
