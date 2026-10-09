using Educast.MediaPipeline.Capture;
using Educast.MediaPipeline.Encoding;
using Educast.Protocol.Broadcast;
using Educast.Protocol.Video;

namespace Educast.MediaPipeline.Broadcast;

/// <summary>
/// Screen capture and H.264 encoding behind one <see cref="IEncodedFrameSource"/>, for
/// <see cref="BroadcastSession"/>. Owns the capture and the encoder it builds through
/// <see cref="EncoderFactory"/> at <c>FrameSizing.EncodeSize(captureSize, preset.Ceiling)</c> and
/// <c>VideoBitrate.BitsPerSecond(encodeSize, preset.Fps, sharpness)</c>.
/// </summary>
/// <remarks>
/// <para>
/// A tick where the screen did not change still asks the encoder for a picture it held back, so a
/// late picture goes out one tick later instead of never. A forced keyframe on a still screen
/// encodes the last captured picture again. A still screen is normal; an encoder that refuses
/// <see cref="MaxConsecutiveRefusals"/> pictures in a row while the capture is delivering them is
/// stuck, and the call throws so the session reports it.
/// </para>
/// <para>
/// A captured frame of a different size than the encoder was built for (the display changed mode)
/// rebuilds the encoder at the new encode size, raises <see cref="EncodeSizeChanged"/> and skips
/// that tick; the next picture out is a keyframe.
/// </para>
/// <para>
/// Every call is made on the session's capture thread, one at a time. <see cref="Dispose"/> may come
/// from another thread; it waits for a call in progress to finish.
/// </para>
/// </remarks>
public sealed class CaptureEncodeSource : IEncodedFrameSource, IDisposable
{
    /// <summary>How many pictures in a row the encoder may refuse while the screen is changing before it is given up on.</summary>
    public const int MaxConsecutiveRefusals = 5;

    // How long the first picture may take: a fresh duplication delivers the whole desktop at once.
    private static readonly TimeSpan FirstPictureTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Builds an encoder for <paramref name="encodeSize"/> at <paramref name="bitsPerSecond"/>, on the capture's device.</summary>
    public delegate IVideoEncoder EncoderFactory(PixelSize encodeSize, int bitsPerSecond);

    private readonly IScreenCapture _capture;
    private readonly QualityPreset _preset;
    private readonly Sharpness _sharpness;
    private readonly EncoderFactory _newEncoder;
    private readonly object _gate = new();

    private IVideoEncoder? _encoder;
    private PixelSize _captureSize;
    private CapturedFrame _lastPicture;
    private bool _keyframeOwed;
    private int _refused;
    private bool _disposed;

    /// <param name="capture">Taken over: disposed with this source, and on a failure to start.</param>
    /// <exception cref="InvalidOperationException">The screen delivered no picture within five seconds.</exception>
    public CaptureEncodeSource(IScreenCapture capture, QualityPreset preset, Sharpness sharpness, EncoderFactory newEncoder)
    {
        ArgumentNullException.ThrowIfNull(capture);
        ArgumentNullException.ThrowIfNull(preset);
        ArgumentNullException.ThrowIfNull(newEncoder);
        _capture = capture;
        _preset = preset;
        _sharpness = sharpness;
        _newEncoder = newEncoder;

        try
        {
            if (!capture.TryGetNextFrame(FirstPictureTimeout, out _lastPicture))
            {
                throw new InvalidOperationException(
                    $"The screen delivered no picture within {FirstPictureTimeout.TotalSeconds:0} seconds; there is nothing to broadcast.");
            }

            Rebuild(new PixelSize(_lastPicture.Width, _lastPicture.Height));
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>Raised on the capture thread when the display changed size and the encoder was rebuilt.</summary>
    public event Action<PixelSize>? EncodeSizeChanged;

    /// <summary>The size frames are encoded at: what <c>StartBroadcast</c> must carry.</summary>
    public PixelSize EncodeSize { get; private set; }

    /// <summary>What the encoder in use reports; the probe before it was built can be wrong.</summary>
    public bool UsingHardwareEncoder
    {
        get
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                return _encoder!.UsingHardwareEncoder;
            }
        }
    }

    public bool TryGetNextFrame(TimeSpan timeout, bool forceKeyframe, out EncodedVideoFrame frame)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            frame = default;

            var changed = _capture.TryGetNextFrame(timeout, out var captured);
            if (changed)
            {
                _lastPicture = captured;
                if (captured.Width != _captureSize.Width || captured.Height != _captureSize.Height)
                {
                    Rebuild(new PixelSize(captured.Width, captured.Height));
                    _keyframeOwed = true;
                    EncodeSizeChanged?.Invoke(EncodeSize);
                    return false;
                }
            }

            var force = forceKeyframe || _keyframeOwed;
            bool produced;
            EncodedFrame encoded;
            if (changed || force)
            {
                produced = _encoder!.TryEncode(_lastPicture, force, out encoded);
                if (!produced && ++_refused >= MaxConsecutiveRefusals)
                {
                    throw new InvalidOperationException(
                        $"The encoder refused {_refused} pictures in a row while the screen was changing; it is stuck.");
                }
            }
            else
            {
                produced = _encoder!.TryTakePending(out encoded);
            }

            if (!produced)
            {
                return false;
            }

            _refused = 0;
            if (encoded.IsKeyframe)
            {
                _keyframeOwed = false;
            }

            frame = new EncodedVideoFrame(encoded.Data, encoded.IsKeyframe);
            return true;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _encoder?.Dispose();
            _capture.Dispose();
        }
    }

    private void Rebuild(PixelSize captureSize)
    {
        _encoder?.Dispose();
        _encoder = null;
        _captureSize = captureSize;
        EncodeSize = FrameSizing.EncodeSize(captureSize, _preset.Ceiling);
        _encoder = _newEncoder(EncodeSize, VideoBitrate.BitsPerSecond(EncodeSize, _preset.Fps, _sharpness));
        _refused = 0;
    }
}
