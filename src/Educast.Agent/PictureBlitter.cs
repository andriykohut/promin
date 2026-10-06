using Educast.Protocol.Video;
using Vortice;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace Educast.Agent;

/// <summary>
/// Draws a decoded NV12 picture onto a screen-sized RGB texture with the Direct3D 11 video processor, which scales
/// and converts in one GPU step: the whole picture, centred, in the largest rectangle of its own shape that fits
/// (<see cref="FrameSizing.FitRect"/>), black everywhere else. The input is limited-range BT.709, as the
/// encoder wrote it. Nothing is copied. Not thread-safe: one call at a time, on the thread that owns the device's
/// immediate context.
/// </summary>
internal sealed class PictureBlitter : IDisposable
{
    private readonly ID3D11Device _device;
    private readonly ID3D11VideoDevice _videoDevice;
    private readonly ID3D11VideoContext _videoContext;

    // The enumerator and processor are made for a pair of sizes and rebuilt when either changes.
    private ID3D11VideoProcessorEnumerator? _enumerator;
    private ID3D11VideoProcessor? _processor;
    private PixelSize _pictureSize;
    private PixelSize _targetSize;

    private bool _disposed;

    /// <param name="device">The shared device; it must have been created with video support.</param>
    /// <exception cref="NotSupportedException">The video processor cannot read NV12 or write BGRA on this GPU.</exception>
    public PictureBlitter(ID3D11Device device)
    {
        ArgumentNullException.ThrowIfNull(device);
        _device = device;

        _videoDevice = device.QueryInterfaceOrNull<ID3D11VideoDevice>()
            ?? throw new NotSupportedException("The Direct3D 11 device does not offer the video processor.");
        try
        {
            // The immediate context belongs to the device and is released with it; the interface queried from it is ours.
            _videoContext = device.ImmediateContext.QueryInterface<ID3D11VideoContext>();
            EnsureProcessor(new PixelSize(1920, 1080), new PixelSize(1920, 1080)); // the formats, found out now
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>
    /// Clears <paramref name="target"/> to opaque black, then draws the top-left <paramref name="pictureWidth"/> by
    /// <paramref name="pictureHeight"/> pixels of <paramref name="nv12Picture"/> into the rectangle that fits them
    /// on it. The source is the picture, never the whole texture: the decoder's texture is its coded size, and
    /// the rows below the picture are not part of it.
    /// </summary>
    /// <param name="target">A B8G8R8A8 texture with the render-target binding, <paramref name="targetSize"/> in size.</param>
    public void Draw(ID3D11Texture2D nv12Picture, int pictureWidth, int pictureHeight, ID3D11Texture2D target, PixelSize targetSize)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var picture = new PixelSize(pictureWidth, pictureHeight);
        var destination = FrameSizing.FitRect(picture, targetSize);

        DisplayStepException.Run(
            $"preparing the video processor for a {pictureWidth}x{pictureHeight} picture on a {targetSize.Width}x{targetSize.Height} screen",
            () => EnsureProcessor(picture, targetSize));

        DisplayStepException.Run("clearing the screen texture to black", () =>
        {
            using var renderTarget = _device.CreateRenderTargetView(target);
            _device.ImmediateContext.ClearRenderTargetView(renderTarget, new Color4(0f, 0f, 0f, 1f));
        });

        // Each step is named, because E_INVALIDARG alone does not say which call refused its argument.
        using var input = DisplayStepException.Run(
            $"creating the video processor's input view on the decoded picture's texture ({Describe(nv12Picture)})",
            () => _videoDevice.CreateVideoProcessorInputView(
                nv12Picture,
                _enumerator!,
                new VideoProcessorInputViewDescription
                {
                    FourCC = 0,
                    ViewDimension = VideoProcessorInputViewDimension.Texture2D,
                    Texture2D = new Texture2DVideoProcessorInputView { MipSlice = 0, ArraySlice = 0 },
                }));
        using var output = DisplayStepException.Run(
            $"creating the video processor's output view on the screen texture ({Describe(target)})",
            () => _videoDevice.CreateVideoProcessorOutputView(
                target,
                _enumerator!,
                new VideoProcessorOutputViewDescription
                {
                    ViewDimension = VideoProcessorOutputViewDimension.Texture2D,
                    Texture2D = new Texture2DVideoProcessorOutputView { MipSlice = 0 },
                }));

        DisplayStepException.Run("setting the source and destination rectangles", () =>
        {
            _videoContext.VideoProcessorSetStreamSourceRect(_processor!, 0, true, new RawRect(0, 0, pictureWidth, pictureHeight));
            _videoContext.VideoProcessorSetStreamDestRect(
                _processor!, 0, true,
                new RawRect(destination.X, destination.Y, destination.X + destination.Width, destination.Y + destination.Height));
        });

        DisplayStepException.Run(
            $"scaling the picture into {destination} (the video processor's blit)",
            () => _videoContext
                .VideoProcessorBlt(_processor!, output, 0, [new VideoProcessorStream { Enable = true, InputSurface = input }])
                .CheckError());
    }

    /// <summary>What a texture is, for the log line of a step that refused it.</summary>
    private static string Describe(ID3D11Texture2D texture)
    {
        var d = texture.Description;
        return $"{d.Width}x{d.Height} {d.Format}, usage {d.Usage}, bind flags {d.BindFlags}, array size {d.ArraySize}, " +
               $"mip levels {d.MipLevels}, samples {d.SampleDescription.Count}";
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        DisposeProcessor();
        _videoContext?.Dispose();
        _videoDevice?.Dispose();
    }

    // Colour handling: usage 0 is "playback"; YCbCr_Matrix 1 is BT.709; the picture side is limited range
    // (16-235), the RGB side full range (0-255), so black stays black on the student's screen.
    private static VideoProcessorColorSpace ColorSpace(VideoProcessorNominalRange nominalRange, uint rgbRange)
    {
        var space = default(VideoProcessorColorSpace);
        space.Usage = 0;
        space.RGB_Range = rgbRange;
        space.YCbCr_Matrix = 1;
        space.YCbCr_xvYCC = 0;
        space.Nominal_Range = (uint)nominalRange;
        return space;
    }

    private void EnsureProcessor(PixelSize picture, PixelSize target)
    {
        if (_processor is not null && _pictureSize == picture && _targetSize == target)
        {
            return;
        }

        DisposeProcessor();
        try
        {
            _enumerator = _videoDevice.CreateVideoProcessorEnumerator(new VideoProcessorContentDescription
            {
                InputFrameFormat = VideoFrameFormat.Progressive,
                InputFrameRate = new Rational(30, 1),
                InputWidth = (uint)picture.Width,
                InputHeight = (uint)picture.Height,
                OutputFrameRate = new Rational(30, 1),
                OutputWidth = (uint)target.Width,
                OutputHeight = (uint)target.Height,
                Usage = VideoUsage.PlaybackNormal,
            });

            if ((_enumerator.CheckVideoProcessorFormat(Format.NV12) & VideoProcessorFormatSupport.Input) == 0)
            {
                throw new NotSupportedException("The GPU's video processor cannot read NV12 pictures.");
            }

            if ((_enumerator.CheckVideoProcessorFormat(Format.B8G8R8A8_UNorm) & VideoProcessorFormatSupport.Output) == 0)
            {
                throw new NotSupportedException("The GPU's video processor cannot write B8G8R8A8 pictures.");
            }

            _processor = _videoDevice.CreateVideoProcessor(_enumerator, 0);

            _videoContext.VideoProcessorSetStreamFrameFormat(_processor, 0, VideoFrameFormat.Progressive);
            _videoContext.VideoProcessorSetStreamColorSpace(_processor, 0, ColorSpace(VideoProcessorNominalRange.Range_16_235, rgbRange: 0));
            _videoContext.VideoProcessorSetOutputColorSpace(_processor, ColorSpace(VideoProcessorNominalRange.Range_0_255, rgbRange: 0));

            // No driver "enhancement" of the picture: the student must see the teacher's screen as it is.
            _videoContext.VideoProcessorSetStreamAutoProcessingMode(_processor, 0, false);
            _videoContext.VideoProcessorSetOutputTargetRect(_processor, true, new RawRect(0, 0, target.Width, target.Height));

            _pictureSize = picture;
            _targetSize = target;
        }
        catch
        {
            DisposeProcessor();
            throw;
        }
    }

    private void DisposeProcessor()
    {
        _processor?.Dispose();
        _processor = null;
        _enumerator?.Dispose();
        _enumerator = null;
    }
}
