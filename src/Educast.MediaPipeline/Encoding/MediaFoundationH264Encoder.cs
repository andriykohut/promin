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
/// (<see cref="GpuFrameConverter"/>), then encoded for low delay with no frame reordering. Output is an
/// Annex B byte stream, each keyframe carrying its own parameter sets.
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

    private long _inputCount;
    private uint _nextFrameId;
    private bool _disposed;

    public bool UsingHardwareEncoder { get; }

    /// <summary>The encode size: every frame is scaled to it, whatever size the capture delivers.</summary>
    public int Width { get; }

    public int Height { get; }

    /// <summary>Which encoder was activated, for diagnostics (for example "Intel(R) Quick Sync Video H.264 Encoder MFT").</summary>
    public string EncoderName => _transform.Name;

    /// <summary>True when the activated encoder is event-driven (usual for hardware), false when it is synchronous.</summary>
    public bool IsAsynchronous => _transform.IsAsynchronous;

    /// <summary>True when the converted pictures reach the encoder as GPU textures, false when they are read back to memory first.</summary>
    public bool UsesGpuInput => _transform.UsesGpuInput;

    /// <summary>What was configured and how the encoder answered, for diagnostics.</summary>
    public string Configuration => _transform.Notes;

    /// <param name="device">The capture's device (<see cref="DxgiScreenCapture.Device"/>): the frames to encode live on it.</param>
    /// <param name="width">Encode width, positive and even.</param>
    /// <param name="height">Encode height, positive and even.</param>
    /// <param name="targetFps">The frame rate the encoder is configured for.</param>
    public MediaFoundationH264Encoder(ID3D11Device device, int width, int height, int targetFps)
        : this(device, width, height, targetFps, allowHardware: true)
    {
    }

    /// <summary>
    /// As the public constructor; <paramref name="allowHardware"/> false goes straight to Windows' software
    /// encoder, so that path can be exercised (and diagnosed) on a machine that has a hardware one.
    /// </summary>
    internal MediaFoundationH264Encoder(ID3D11Device device, int width, int height, int targetFps, bool allowHardware)
    {
        ValidateEncodeSize(width, height);
        if (targetFps is < MinFps or > MaxFps)
            throw new ArgumentException($"The frame rate must be {MinFps} to {MaxFps}; got {targetFps}.", nameof(targetFps));
        ArgumentNullException.ThrowIfNull(device);

        _device = device;
        _fps = targetFps;
        Width = width;
        Height = height;

        try
        {
            EnsureComIsInitialized();
            MediaFactory.MFStartup().CheckError();
            _mediaFoundationStarted = true;

            _converter = new GpuFrameConverter(device);
            _transform = EncoderTransform.Open(device, width, height, targetFps, allowHardware);
            UsingHardwareEncoder = _transform.IsHardware;
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

        using (var sample = CreateInputSample(picture))
        {
            if (!_transform.TryInput(sample, forceKeyframe))
                return false;
            _inputCount++;
        }

        _transform.CollectOutputs(waitForOne: true);
        return TryTakeEncodedFrame(out encoded);
    }

    private bool TryTakeEncodedFrame(out EncodedFrame encoded)
    {
        encoded = default;
        if (!_transform.TryTakeOutput(out var output))
            return false;

        var data = output.Data;
        var summary = H264Stream.Inspect(data);

        // What the encoder really produced decides, not what was asked for. Without an Annex B stream to
        // read (it should always be one), fall back on the transform's own "clean point" mark.
        var isKeyframe = summary.IsAnnexB ? summary.HasIdr : output.IsCleanPoint;
        if (isKeyframe)
            data = H264Stream.WithParameterSets(data, _transform.ParameterSets);

        encoded = new EncodedFrame(data, isKeyframe, _nextFrameId++);
        return true;
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
            sample.SampleTime = EncoderSettings.SampleTime(_inputCount, _fps);
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

        _staging?.Dispose();
        _transform?.Dispose();
        _converter?.Dispose();
        if (_mediaFoundationStarted)
            MediaFactory.MFShutdown();
    }

    // Media Foundation transforms are COM objects. Threads that have not been through COM yet get the
    // multithreaded apartment here; one that is already in another apartment keeps it (that answer,
    // RPC_E_CHANGED_MODE, is as good as success).
    private static void EnsureComIsInitialized() => CoInitializeEx(IntPtr.Zero, CoInitMultithreaded);

    private const uint CoInitMultithreaded = 0x0;

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(IntPtr reserved, uint coInit);
}
