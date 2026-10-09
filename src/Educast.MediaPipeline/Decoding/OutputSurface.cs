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
/// <para>
/// <b>Bind flags.</b> The texture is meant to be drawn from by the Direct3D 11 video processor (the student's
/// display scales it to the screen with that), and the documentation of
/// <c>ID3D11VideoDevice::CreateVideoProcessorInputView</c> allows an input texture only if its bind flags are 0 or
/// include one of decoder, video encoder, render target or unordered access. A texture bound for shader
/// resource alone (what this class made before) is refused with E_INVALIDARG on an NVIDIA GeForce RTX 4070.
/// So: where the device can render to NV12 the texture is bound for render target (and for shader resource as
/// well, where the device can load NV12 in a shader); where it cannot, it has no bind flags at all, which is
/// allowed. Both are filled the same way (a GPU copy into it, an upload into it), and neither needs any flag
/// itself. Each choice is tried against the video processor when the texture is made: a device that accepts
/// neither fails then, with a message that says what was tried, not at the first draw.
/// </para>
/// </summary>
internal sealed class OutputSurface : IDisposable
{
    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly ID3D11VideoDevice? _videoDevice;
    private readonly BindFlags[] _bindChoices;
    private ID3D11Texture2D? _texture;
    private uint _width;
    private uint _height;

    internal OutputSurface(ID3D11Device device)
    {
        _device = device;
        // One wrapper for the device's immediate context, which belongs to the device and goes with it.
        _context = device.ImmediateContext;

        _videoDevice = device.QueryInterfaceOrNull<ID3D11VideoDevice>();
        _bindChoices = ChooseBindFlags(device.CheckFormatSupport(Format.NV12));
    }

    /// <summary>
    /// The bind flags to try, best first. Shader-resource binding is only added where the driver supports it, as
    /// for the converter's output; it is never offered alone.
    /// </summary>
    internal static BindFlags[] ChooseBindFlags(FormatSupport nv12Support)
    {
        if ((nv12Support & FormatSupport.RenderTarget) == 0)
            return [BindFlags.None];

        var renderTarget = (nv12Support & FormatSupport.ShaderLoad) != 0
            ? BindFlags.RenderTarget | BindFlags.ShaderResource
            : BindFlags.RenderTarget;
        return [renderTarget, BindFlags.None];
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

        var tried = new List<string>();
        foreach (var bind in _bindChoices)
        {
            var texture = CreateTexture(width, height, bind);
            var refusal = _videoDevice is null ? null : ProbeAsVideoProcessorInput(texture, width, height);
            if (refusal is null)
            {
                _texture = texture;
                _width = width;
                _height = height;
                return;
            }

            texture.Dispose();
            tried.Add($"bind flags {bind}: {refusal}");
        }

        throw new NotSupportedException(
            $"The video processor on this GPU refuses the decoder's {width}x{height} NV12 output texture as input, " +
            $"with every kind of texture this decoder can make ({string.Join("; ", tried)}).");
    }

    private ID3D11Texture2D CreateTexture(uint width, uint height, BindFlags bind) => _device.CreateTexture2D(new Texture2DDescription
    {
        Width = width,
        Height = height,
        MipLevels = 1,
        ArraySize = 1,
        Format = Format.NV12,
        SampleDescription = new SampleDescription(1, 0),
        Usage = ResourceUsage.Default,
        BindFlags = bind,
        CPUAccessFlags = CpuAccessFlags.None,
        MiscFlags = ResourceOptionFlags.None,
    });

    // Null when the video processor takes the texture as input; otherwise what it said.
    private string? ProbeAsVideoProcessorInput(ID3D11Texture2D texture, uint width, uint height)
    {
        try
        {
            using var enumerator = _videoDevice!.CreateVideoProcessorEnumerator(new VideoProcessorContentDescription
            {
                InputFrameFormat = VideoFrameFormat.Progressive,
                InputFrameRate = new Rational(30, 1),
                InputWidth = width,
                InputHeight = height,
                OutputFrameRate = new Rational(30, 1),
                OutputWidth = width,
                OutputHeight = height,
                Usage = VideoUsage.PlaybackNormal,
            });
            using var view = _videoDevice.CreateVideoProcessorInputView(
                texture,
                enumerator,
                new VideoProcessorInputViewDescription
                {
                    FourCC = 0,
                    ViewDimension = VideoProcessorInputViewDimension.Texture2D,
                    Texture2D = new Texture2DVideoProcessorInputView { MipSlice = 0, ArraySlice = 0 },
                });
            return null;
        }
        catch (SharpGen.Runtime.SharpGenException refused)
        {
            return refused.Message.Trim();
        }
    }

    public void Dispose()
    {
        _texture?.Dispose();
        _texture = null;
        _videoDevice?.Dispose();
    }
}
