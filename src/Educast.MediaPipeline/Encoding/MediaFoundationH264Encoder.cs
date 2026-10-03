using System.Runtime.InteropServices;
using Educast.MediaPipeline.Capture;
using Educast.MediaPipeline.Scaling;
using SharpGen.Runtime;
using Vortice.Direct3D11;
using Vortice.MediaFoundation;

namespace Educast.MediaPipeline.Encoding;

/// <summary>
/// H.264 encoder on Media Foundation: a hardware encoder where the machine has one, Windows' built-in
/// software encoder otherwise. Every frame is scaled to the encode size and converted to NV12 on the GPU
/// (<see cref="GpuFrameConverter"/>), then encoded for low delay with no frame reordering at the bitrate the
/// caller gives. Output is an Annex B byte stream, and every keyframe carries its own parameter sets (the
/// encoder's own when it writes them, otherwise ones remembered from earlier in the stream).
/// The encoder adds no keyframes of its own: the broadcast session decides when one is wanted.
/// Not thread-safe: one call at a time, from any thread.
/// </summary>
public sealed class MediaFoundationH264Encoder : IVideoEncoder
{
    private const int MinFps = 1;
    private const int MaxFps = 240;

    private readonly ID3D11Device _device;
    private readonly int _fps;
    private bool _mediaFoundationStarted;
    private GpuFrameConverter _converter = null!;
    private EncoderTransform _transform = null!;

    // Memory path only: the NV12 picture is copied here to be read by the CPU.
    private ID3D11Texture2D? _staging;

    private EncoderPipeline<IMFSample> _pipeline = null!;
    private long _inputCount;
    private bool _disposed;

    public bool UsingHardwareEncoder { get; }

    /// <summary>The encode size: every frame is scaled to it, whatever size the capture delivers.</summary>
    public int Width { get; }

    public int Height { get; }

    /// <summary>The bitrate the encoder was configured with, as given by the caller.</summary>
    public int BitsPerSecond { get; }

    /// <summary>
    /// How many keyframes came out of the encoder without parameter sets of their own and were given
    /// remembered ones. Zero means the encoder writes them into every keyframe itself.
    /// </summary>
    public int KeyframesCompletedFromMemory => _pipeline.KeyframesCompleted;

    /// <summary>Which encoder was activated, for diagnostics (for example "Intel(R) Quick Sync Video H.264 Encoder MFT").</summary>
    public string EncoderName => _transform.Name;

    /// <summary>True when the activated encoder is event-driven (usual for hardware), false when it is synchronous.</summary>
    public bool IsAsynchronous => _transform.IsAsynchronous;

    /// <summary>True when the converted pictures reach the encoder as GPU textures, false when they are read back to memory first.</summary>
    public bool UsesGpuInput => _transform.UsesGpuInput;

    /// <summary>
    /// What was configured and how the encoder answered, for diagnostics: first one line for each hardware encoder
    /// that was passed over and why, then one line for the encoder in use.
    /// </summary>
    public string Configuration => _transform.Notes;

    /// <param name="device">The capture's device (<see cref="DxgiScreenCapture.Device"/>): the frames to encode live on it.</param>
    /// <param name="width">Encode width, positive and even.</param>
    /// <param name="height">Encode height, positive and even.</param>
    /// <param name="targetFps">The frame rate the encoder is configured for.</param>
    /// <param name="bitsPerSecond">The bitrate to encode at, positive. Choose it with <c>VideoBitrate.BitsPerSecond</c>.</param>
    public MediaFoundationH264Encoder(ID3D11Device device, int width, int height, int targetFps, int bitsPerSecond)
        : this(device, width, height, targetFps, bitsPerSecond, allowHardware: true)
    {
    }

    /// <summary>
    /// As the public constructor; <paramref name="allowHardware"/> false goes straight to Windows' software
    /// encoder, so that path can be exercised (and diagnosed) on a machine that has a hardware one.
    /// </summary>
    internal MediaFoundationH264Encoder(ID3D11Device device, int width, int height, int targetFps, int bitsPerSecond, bool allowHardware)
    {
        ValidateEncodeSize(width, height);
        if (targetFps is < MinFps or > MaxFps)
            throw new ArgumentException($"The frame rate must be {MinFps} to {MaxFps}; got {targetFps}.", nameof(targetFps));
        if (bitsPerSecond <= 0)
            throw new ArgumentOutOfRangeException(nameof(bitsPerSecond), bitsPerSecond, "The bitrate must be positive.");
        ArgumentNullException.ThrowIfNull(device);

        _device = device;
        _fps = targetFps;
        Width = width;
        Height = height;
        BitsPerSecond = bitsPerSecond;

        try
        {
            EnsureComIsInitialized();
            MediaFactory.MFStartup().CheckError();
            _mediaFoundationStarted = true;

            _converter = new GpuFrameConverter(device);
            _transform = EncoderTransform.Open(device, width, height, targetFps, bitsPerSecond, allowHardware);
            UsingHardwareEncoder = _transform.IsHardware;

            var parameterSets = new H264ParameterSets();
            parameterSets.Seed(_transform.ParameterSets);
            _pipeline = new EncoderPipeline<IMFSample>(_transform, EncoderTimeouts.Default, parameterSets);
        }
        catch
        {
            // Whatever was created before the failure is released; Dispose tolerates the rest being null.
            Dispose();
            throw;
        }
    }

    /// <summary>
    /// Whether Media Foundation lists a hardware H.264 encoder. The caller can pick
    /// <c>QualityPreset.SoftwareFallback</c> when it does not. A machine where Media Foundation cannot be
    /// started or asked reports false.
    /// </summary>
    public static bool IsHardwareEncoderAvailable()
    {
        try
        {
            EnsureComIsInitialized();
            MediaFactory.MFStartup().CheckError();
        }
        catch (Exception ex) when (ex is SharpGenException or DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }

        try
        {
            return EncoderTransform.AnyHardwareEncoder();
        }
        catch (SharpGenException)
        {
            return false;
        }
        finally
        {
            MediaFactory.MFShutdown();
        }
    }

    internal static void ValidateEncodeSize(int width, int height)
    {
        if (width <= 0 || width % 2 != 0)
            throw new ArgumentException($"The encode width must be positive and even (H.264 4:2:0); got {width}.", nameof(width));
        if (height <= 0 || height % 2 != 0)
            throw new ArgumentException($"The encode height must be positive and even (H.264 4:2:0); got {height}.", nameof(height));
    }

    public bool TryEncode(CapturedFrame frame, bool forceKeyframe, out EncodedFrame encoded)
    {
        encoded = default;
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (frame.Texture is null)
            throw new ArgumentException("The frame has no texture.", nameof(frame));

        // The capture's texture is only valid until its next frame, so it is finished with here: the
        // converter reads it into a texture of its own before this call returns.
        var picture = _converter.Convert(frame.Texture, frame.Width, frame.Height, Width, Height);

        using var sample = CreateInputSample(picture);
        return _pipeline.TryEncode(sample, forceKeyframe, out encoded);
    }

    public bool TryTakePending(out EncodedFrame encoded)
    {
        encoded = default;
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _pipeline.TryTakePending(out encoded);
    }
    private IMFSample CreateInputSample(ID3D11Texture2D picture)
    {
        var sample = MediaFactory.MFCreateSample();
        try
        {
            using (var buffer = _transform.UsesGpuInput ? CreateTextureBuffer(picture) : CreateMemoryBuffer(picture))
                sample.AddBuffer(buffer);

            // A steady clock at the target rate: frames arrive when the screen changes, but the encoder's
            // rate control wants regular spacing.
            sample.SampleTime = EncoderSettings.SampleTime(_inputCount++, _fps);
            sample.SampleDuration = EncoderSettings.SampleDuration(_fps);
            return sample;
        }
        catch
        {
            sample.Dispose();
            throw;
        }
    }

    private IMFMediaBuffer CreateTextureBuffer(ID3D11Texture2D picture)
    {
        var buffer = MediaFactory.MFCreateDXGISurfaceBuffer(typeof(ID3D11Texture2D).GUID, picture, 0, false);
        try
        {
            // A buffer made from a surface starts with length 0; the transform reads the length.
            using var flat = buffer.QueryInterfaceOrNull<IMF2DBuffer>();
            var length = flat?.ContiguousLength ?? 0;
            buffer.CurrentLength = length > 0 ? length : Nv12Layout.Size(Width, Height);
            return buffer;
        }
        catch
        {
            buffer.Dispose();
            throw;
        }
    }

    private unsafe IMFMediaBuffer CreateMemoryBuffer(ID3D11Texture2D picture)
    {
        var size = Nv12Layout.Size(Width, Height);
        var buffer = MediaFactory.MFCreateMemoryBuffer(size);
        try
        {
            _staging ??= CreateStagingTexture();
            var context = _device.ImmediateContext;
            context.CopyResource(_staging, picture);

            var mapped = context.Map(_staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            try
            {
                var pitch = (int)mapped.RowPitch;
                buffer.Lock(out var destination, out _, out _);
                try
                {
                    // The chroma plane follows the luma plane at the same pitch.
                    var source = new ReadOnlySpan<byte>((void*)mapped.DataPointer, pitch * (Height + Height / 2));
                    Nv12Layout.Pack(source, pitch, Width, Height, new Span<byte>((void*)destination, size));
                }
                finally
                {
                    buffer.Unlock();
                }
            }
            finally
            {
                context.Unmap(_staging, 0);
            }

            buffer.CurrentLength = size;
            return buffer;
        }
        catch
        {
            buffer.Dispose();
            throw;
        }
    }

    private ID3D11Texture2D CreateStagingTexture() =>
        _device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)Width,
            Height = (uint)Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Vortice.DXGI.Format.NV12,
            SampleDescription = new Vortice.DXGI.SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read,
            MiscFlags = ResourceOptionFlags.None,
        });

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        // Every step runs whether or not the one before it threw: the converter and Media Foundation itself
        // must still be released.
        Release(() => _staging?.Dispose());
        Release(() => _transform?.Dispose());
        Release(() => _converter?.Dispose());
        if (_mediaFoundationStarted)
            Release(() => MediaFactory.MFShutdown());
    }

    private static void Release(Action step)
    {
        try
        {
            step();
        }
        catch (Exception)
        {
        }
    }

    // Media Foundation transforms are COM objects. Threads that have not been through COM yet get the
    // multithreaded apartment here; one that is already in another apartment keeps it (that answer,
    // RPC_E_CHANGED_MODE, is as good as success).
    private static void EnsureComIsInitialized() => CoInitializeEx(IntPtr.Zero, CoInitMultithreaded);

    private const uint CoInitMultithreaded = 0x0;

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(IntPtr reserved, uint coInit);
}
