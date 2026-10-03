using System.Runtime.InteropServices;
using Educast.MediaPipeline.Encoding;
using Vortice.Direct3D11;
using Vortice.MediaFoundation;

namespace Educast.MediaPipeline.Decoding;

/// <summary>
/// H.264 decoder on Media Foundation, for the student side of the broadcast: Windows' built-in decoder, which
/// decodes on the GPU when it is given a Direct3D device it can use and falls back to software by itself when
/// it cannot. Each picture comes out as an NV12 texture on the device the decoder was given
/// (<see cref="DecodedFrame"/>, which also says how long it stays valid and that the picture can be smaller
/// than its texture).
/// <para>
/// Set up for low delay with no frame reordering, so a picture comes back from the call that fed it. The
/// teacher's screen is often still and a new picture arrives only every few seconds; a decoder that waited for
/// the next one would show students a picture seconds late. If this machine's decoder will not do that, the
/// calls return false until the next picture arrives (<see cref="LowLatencyAccepted"/> and
/// <see cref="Configuration"/> say what the decoder was asked and what it answered).
/// </para>
/// <para>
/// The size is not given: it comes from the stream (the SPS in each keyframe), and a change of size mid-stream
/// is followed, the first picture after the change reporting the new size. A decoder can only start at a
/// keyframe that carries its parameter sets, and after any failure only at the next one.
/// </para>
/// <para>
/// Not thread-safe: it may be called from different threads over time, one call at a time. The device must
/// have been created with video support (otherwise decoding is in software) and is switched to multithread
/// protection, which Media Foundation needs when it shares the device. Create the decoder on a thread that is
/// in, or may join, the multithreaded COM apartment (a thread-pool thread is).
/// </para>
/// </summary>
public sealed class MediaFoundationH264Decoder : IVideoDecoder
{
    private bool _mediaFoundationStarted;
    private DecoderTransform? _transform;
    private DecoderPipeline<DecodedPicture>? _pipeline;
    private OutputSurface? _output;
    private string _deviceNote = "";
    private string? _presentFailure;
    private bool _disposed;

    /// <summary>The decoder in use, for diagnostics.</summary>
    public string DecoderName => DecoderTransform.DecoderName;

    /// <summary>True when the decoder is event-driven, false when it is synchronous (Windows' H.264 decoder is synchronous).</summary>
    public bool IsAsynchronous => false;

    /// <summary>True when the decoder took the low-delay request: it will then give a picture back from the call that fed it.</summary>
    public bool LowLatencyAccepted => _transform!.LowLatencyAccepted;

    /// <summary>
    /// Whether the decoder is decoding on the GPU: true when pictures come out as Direct3D textures, false when
    /// the decoder fell back to software (or was not given the device). Null until the first picture has come out.
    /// </summary>
    public bool? IsHardwareAccelerated => _transform!.OutputIsOnGpu;

    /// <summary>
    /// What was configured and how the decoder answered, for diagnostics: whether the Direct3D device was
    /// accepted, whether the low-delay request was, whether it is synchronous, and (once known) the output type
    /// and whether it decodes on the GPU.
    /// </summary>
    public string Configuration => $"{_deviceNote}; {_transform!.Notes}";

    /// <summary>
    /// Why the last call returned no picture, when something was wrong with the stream or the decoder (for
    /// example "waiting for a keyframe"); null when the last call returned a picture or the decoder simply had
    /// nothing to show yet.
    /// </summary>
    public string? LastFailure => _presentFailure ?? _pipeline!.LastFailure;

    /// <param name="device">The device to decode on; the pictures come out on it. On a student PC it is the device the display window uses.</param>
    public MediaFoundationH264Decoder(ID3D11Device device)
        : this(device, allowHardware: true)
    {
    }

    /// <summary>
    /// As the public constructor; <paramref name="allowHardware"/> false does not give the decoder the device, so
    /// it decodes in software and that path can be exercised on a machine that has a GPU decoder.
    /// </summary>
    internal MediaFoundationH264Decoder(ID3D11Device device, bool allowHardware)
    {
        ArgumentNullException.ThrowIfNull(device);

        try
        {
            EnsureComIsInitialized();
            MediaFactory.MFStartup().CheckError();
            _mediaFoundationStarted = true;

            _deviceNote = EnableMultithreadProtection(device);
            _transform = DecoderTransform.Open(device, allowHardware);
            _pipeline = new DecoderPipeline<DecodedPicture>(_transform);
            _output = new OutputSurface(device);
        }
        catch
        {
            // Whatever was created before the failure is released; Dispose tolerates the rest being null.
            Dispose();
            throw;
        }
    }

    // Media Foundation uses the device from its own threads; without this a decode can corrupt the display's use of it.
    private static string EnableMultithreadProtection(ID3D11Device device)
    {
        using var multithread = device.QueryInterfaceOrNull<ID3D11Multithread>();
        if (multithread is null)
            return "device multithread protection: not available";

        var was = multithread.SetMultithreadProtected(true);
        return was ? "device multithread protection: already on" : "device multithread protection: switched on";
    }

    public bool TryDecode(EncodedFrame encoded, out DecodedFrame frame)
    {
        frame = default;
        ObjectDisposedException.ThrowIf(_disposed, this);
        _presentFailure = null;

        if (!_pipeline!.TryDecode(encoded.Data, out var picture))
            return false;

        // The picture's slot in the decoder's pool is held until it is disposed, which is as soon as it has
        // been copied.
        using (picture)
        {
            if (_output!.TryPresent(picture!, out frame, out var problem))
                return true;

            _presentFailure = problem;
            frame = default;
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        // Every step runs whether or not the one before it threw: Media Foundation itself must still be released.
        Release(() => _output?.Dispose());
        Release(() =>
        {
            if (_pipeline is not null)
                _pipeline.Dispose(); // disposes the transform
            else
                _transform?.Dispose();
        });
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
