using Vortice;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Promin.MediaPipeline.Scaling;

/// <summary>
/// Scales a BGRA texture and converts it to NV12 (for the encoder) or keeps it BGRA (for screen pictures),
/// in one GPU step, with the Direct3D 11 video processor. The input is read as full-range RGB. NV12 output
/// (for the encoder) is limited-range BT.709, so black stays black on the agent PC's screen; BGRA output
/// (screen pictures) stays full-range RGB, as the screen is. Not thread-safe: one call at a time.
/// </summary>
public sealed class GpuFrameConverter : IDisposable
{
    // The converted picture is handed to an encoder that may still be reading the previous one when
    // the next call arrives, so the output rotates through a few textures instead of reusing one.
    private const int RingSize = 3;

    private readonly ID3D11Device _device;
    private readonly Format _outputFormat;
    private readonly ID3D11VideoDevice _videoDevice;
    private readonly ID3D11VideoContext _videoContext;

    // The enumerator, processor and output views belong together: all are rebuilt when the source
    // or target size changes.
    private ID3D11VideoProcessorEnumerator? _enumerator;
    private ID3D11VideoProcessor? _processor;
    private readonly ID3D11VideoProcessorOutputView?[] _targetViews = new ID3D11VideoProcessorOutputView?[RingSize];
    private int _sourceWidth;
    private int _sourceHeight;

    private readonly ID3D11Texture2D?[] _targets = new ID3D11Texture2D?[RingSize];
    private int _targetWidth;
    private int _targetHeight;
    private int _next;

    private bool _disposed;

    /// <param name="device">The capture's device: it must have been created with video support.</param>
    /// <param name="outputFormat"><see cref="Format.NV12"/> for the encoder, <see cref="Format.B8G8R8A8_UNorm"/> for a screen picture.</param>
    public GpuFrameConverter(ID3D11Device device, Format outputFormat = Format.NV12)
    {
        if (outputFormat is not (Format.NV12 or Format.B8G8R8A8_UNorm))
            throw new ArgumentOutOfRangeException(nameof(outputFormat), $"The converter writes NV12 or B8G8R8A8_UNorm; {outputFormat} was asked for.");
        ArgumentNullException.ThrowIfNull(device);
        _device = device;
        _outputFormat = outputFormat;

        _videoDevice = device.QueryInterfaceOrNull<ID3D11VideoDevice>()
            ?? throw new NotSupportedException("The Direct3D 11 device does not offer the video processor (it was not created with video support, or the driver has none).");
        try
        {
            // The immediate context belongs to the device and is released with it, so it is not disposed here;
            // the interface queried from it is ours.
            _videoContext = device.ImmediateContext.QueryInterface<ID3D11VideoContext>();
        }
        catch
        {
            _videoDevice.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Converts <paramref name="bgraSource"/> (its top-left <paramref name="sourceWidth"/> by
    /// <paramref name="sourceHeight"/> pixels) to the output format given to the constructor (NV12 or BGRA)
    /// at <paramref name="targetWidth"/> by <paramref name="targetHeight"/>, scaling as needed. The same-size
    /// case still converts. The returned texture is owned by the converter and stays valid for at least
    /// the next two calls; the caller must not dispose it.
    /// </summary>
    public ID3D11Texture2D Convert(ID3D11Texture2D bgraSource, int sourceWidth, int sourceHeight, int targetWidth, int targetHeight)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(bgraSource);
        if (sourceWidth <= 0 || sourceHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(sourceWidth), $"The source size must be positive; got {sourceWidth}x{sourceHeight}.");
        var even = _outputFormat != Format.NV12 || (targetWidth % 2 == 0 && targetHeight % 2 == 0);
        if (targetWidth <= 0 || targetHeight <= 0 || !even)
            throw new ArgumentOutOfRangeException(nameof(targetWidth), $"The target size must be positive{(_outputFormat == Format.NV12 ? " and even (NV12)" : "")}; got {targetWidth}x{targetHeight}.");

        var description = bgraSource.Description;
        if (description.Format != Format.B8G8R8A8_UNorm)
            throw new ArgumentException($"The source must be B8G8R8A8_UNorm; it is {description.Format}.", nameof(bgraSource));
        if (description.Width < sourceWidth || description.Height < sourceHeight)
            throw new ArgumentException($"The source texture is {description.Width}x{description.Height}, smaller than the {sourceWidth}x{sourceHeight} it is said to hold.", nameof(bgraSource));

        EnsureTargets(targetWidth, targetHeight);
        EnsureProcessor(sourceWidth, sourceHeight);

        var slot = _next;
        _next = (_next + 1) % RingSize;

        using var input = _videoDevice.CreateVideoProcessorInputView(
            bgraSource,
            _enumerator!,
            new VideoProcessorInputViewDescription
            {
                FourCC = 0,
                ViewDimension = VideoProcessorInputViewDimension.Texture2D,
                Texture2D = new Texture2DVideoProcessorInputView { MipSlice = 0, ArraySlice = 0 },
            });

        var stream = new VideoProcessorStream
        {
            Enable = true,
            InputSurface = input,
        };
        _videoContext.VideoProcessorBlt(_processor!, _targetViews[slot]!, 0, [stream]).CheckError();

        return _targets[slot]!;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        DisposeProcessor();
        DisposeTargets();
        _videoContext?.Dispose();
        _videoDevice?.Dispose();
    }

    // The colour handling the processor is told about. Usage 0 is "playback"; YCbCr_Matrix 1 means
    // BT.709; RGB_Range 0 means full range (0-255); Nominal_Range is the range of the output (YCbCr for NV12, RGB for BGRA).
    private static VideoProcessorColorSpace ColorSpace(VideoProcessorNominalRange nominalRange)
    {
        var space = default(VideoProcessorColorSpace);
        space.Usage = 0;
        space.RGB_Range = 0;
        space.YCbCr_Matrix = 1;
        space.YCbCr_xvYCC = 0;
        space.Nominal_Range = (uint)nominalRange;
        return space;
    }

    // Rebuilt only when the source size changes (or the targets were replaced): the enumerator is
    // created for a pair of sizes.
    private void EnsureProcessor(int sourceWidth, int sourceHeight)
    {
        if (_processor is not null && _sourceWidth == sourceWidth && _sourceHeight == sourceHeight)
            return;

        DisposeProcessor();
        try
        {
            var content = new VideoProcessorContentDescription
            {
                InputFrameFormat = VideoFrameFormat.Progressive,
                InputFrameRate = new Rational(30, 1),
                InputWidth = (uint)sourceWidth,
                InputHeight = (uint)sourceHeight,
                OutputFrameRate = new Rational(30, 1),
                OutputWidth = (uint)_targetWidth,
                OutputHeight = (uint)_targetHeight,
                Usage = VideoUsage.PlaybackNormal,
            };

            _enumerator = _videoDevice.CreateVideoProcessorEnumerator(content);

            if ((_enumerator.CheckVideoProcessorFormat(Format.B8G8R8A8_UNorm) & VideoProcessorFormatSupport.Input) == 0)
                throw new NotSupportedException("The GPU's video processor cannot read B8G8R8A8 pictures.");
            if ((_enumerator.CheckVideoProcessorFormat(_outputFormat) & VideoProcessorFormatSupport.Output) == 0)
                throw new NotSupportedException($"The GPU's video processor cannot write {_outputFormat} pictures.");

            _processor = _videoDevice.CreateVideoProcessor(_enumerator, 0);
            ConfigureProcessor(_processor, sourceWidth, sourceHeight);

            for (var i = 0; i < RingSize; i++)
            {
                _targetViews[i] = _videoDevice.CreateVideoProcessorOutputView(
                    _targets[i]!,
                    _enumerator,
                    new VideoProcessorOutputViewDescription
                    {
                        ViewDimension = VideoProcessorOutputViewDimension.Texture2D,
                        Texture2D = new Texture2DVideoProcessorOutputView { MipSlice = 0 },
                    });
            }

            _sourceWidth = sourceWidth;
            _sourceHeight = sourceHeight;
        }
        catch
        {
            DisposeProcessor();
            throw;
        }
    }

    private void ConfigureProcessor(ID3D11VideoProcessor processor, int sourceWidth, int sourceHeight)
    {
        _videoContext.VideoProcessorSetStreamFrameFormat(processor, 0, VideoFrameFormat.Progressive);
        _videoContext.VideoProcessorSetStreamColorSpace(processor, 0, ColorSpace(VideoProcessorNominalRange.Range_0_255));
        // NV12 for the encoder is limited range (16..235); a BGRA screen picture stays full range like the screen.
        _videoContext.VideoProcessorSetOutputColorSpace(processor, ColorSpace(
            _outputFormat == Format.NV12 ? VideoProcessorNominalRange.Range_16_235 : VideoProcessorNominalRange.Range_0_255));

        // No driver "enhancement" of the picture: the viewer must see the presenter's screen as it is.
        _videoContext.VideoProcessorSetStreamAutoProcessingMode(processor, 0, false);

        _videoContext.VideoProcessorSetStreamSourceRect(processor, 0, true, new RawRect(0, 0, sourceWidth, sourceHeight));
        _videoContext.VideoProcessorSetStreamDestRect(processor, 0, true, new RawRect(0, 0, _targetWidth, _targetHeight));
        _videoContext.VideoProcessorSetOutputTargetRect(processor, true, new RawRect(0, 0, _targetWidth, _targetHeight));
    }

    private void EnsureTargets(int width, int height)
    {
        if (_targets[0] is not null && _targetWidth == width && _targetHeight == height)
            return;

        // The processor and views were made for the old target size.
        DisposeProcessor();
        DisposeTargets();

        // Shader-resource binding is only added where the driver supports it; the video processor
        // needs just the render-target binding.
        var bind = BindFlags.RenderTarget;
        if ((_device.CheckFormatSupport(_outputFormat) & FormatSupport.ShaderLoad) != 0)
            bind |= BindFlags.ShaderResource;

        for (var i = 0; i < RingSize; i++)
        {
            _targets[i] = _device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)width,
                Height = (uint)height,
                MipLevels = 1,
                ArraySize = 1,
                Format = _outputFormat,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = bind,
                CPUAccessFlags = CpuAccessFlags.None,
                MiscFlags = ResourceOptionFlags.None,
            });
        }

        _targetWidth = width;
        _targetHeight = height;
        _next = 0;
    }

    private void DisposeProcessor()
    {
        for (var i = 0; i < RingSize; i++)
        {
            _targetViews[i]?.Dispose();
            _targetViews[i] = null;
        }
        _processor?.Dispose();
        _processor = null;
        _enumerator?.Dispose();
        _enumerator = null;
    }

    private void DisposeTargets()
    {
        for (var i = 0; i < RingSize; i++)
        {
            _targets[i]?.Dispose();
            _targets[i] = null;
        }
        _targetWidth = 0;
        _targetHeight = 0;
    }
}
