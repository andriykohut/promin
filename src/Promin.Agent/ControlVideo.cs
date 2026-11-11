using Promin.MediaPipeline.Broadcast;
using Promin.MediaPipeline.Capture;
using Promin.MediaPipeline.Encoding;
using Promin.Protocol.Broadcast;
using Promin.Protocol.Transport;
using Promin.Protocol.Video;

namespace Promin.Agent;

/// <summary>The agent PC's screen as video for the console PC, for as long as the PC is controlled.</summary>
internal interface IControlVideo : IDisposable
{
    /// <summary>Makes the next frame a keyframe, once.</summary>
    void RequestKeyframe();

    /// <summary>Sends frames until <paramref name="ct"/> is cancelled.</summary>
    Task RunAsync(CancellationToken ct);
}

/// <summary>
/// Takes one frame per tick from the source and sends it as <c>[AgentStream.ControlVideo] + envelope + frame</c>,
/// one send at a time. The first frame is a keyframe, and so is one at least every 2 s after the last; a keyframe
/// asked for is asked of the source again on each tick until one comes out. Each frame sent takes its id from
/// <paramref name="nextId"/>.
/// </summary>
internal sealed class ControlVideo(
    IEncodedFrameSource source,
    int fps,
    Func<byte[], CancellationToken, ValueTask> send,
    Func<uint> nextId,
    TimeProvider time) : IControlVideo
{
    // A lost frame the console PC did not notice smears the picture until the next keyframe; this bounds how long.
    private static readonly TimeSpan KeyframeInterval = TimeSpan.FromSeconds(2);

    private readonly TimeSpan _tick = TimeSpan.FromMilliseconds(1000.0 / fps);
    private int _keyframeRequested;
    private long? _lastKeyframe;

    public void RequestKeyframe() => Volatile.Write(ref _keyframeRequested, 1);

    /// <summary>Returns when <paramref name="ct"/> is cancelled, without throwing; throws what the source or the send throws.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var started = time.GetTimestamp();
                var requested = Interlocked.Exchange(ref _keyframeRequested, 0) == 1;
                var force = requested || _lastKeyframe is not { } last || time.GetElapsedTime(last, started) >= KeyframeInterval;
                var produced = source.TryGetNextFrame(_tick, force, out var frame);
                if (produced && frame.IsKeyframe)
                {
                    _lastKeyframe = started;
                }
                else if (requested)
                {
                    // The encoder can drop a request it gets while it is not ready for a picture.
                    RequestKeyframe();
                }

                if (produced)
                {
                    var bytes = new byte[1 + VideoFrameEnvelope.Size + frame.Data.Length];
                    bytes[0] = AgentStream.ControlVideo;
                    new VideoFrameEnvelope(nextId(), frame.IsKeyframe).WriteTo(bytes.AsSpan(1));
                    frame.Data.CopyTo(bytes, 1 + VideoFrameEnvelope.Size);
                    await send(bytes, ct).ConfigureAwait(false);
                }

                var rest = _tick - time.GetElapsedTime(started);
                if (rest > TimeSpan.Zero)
                {
                    await Task.Delay(rest, time, ct).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    public void Dispose() => (source as IDisposable)?.Dispose();

    /// <summary>
    /// Opens the screen and its encoder at <see cref="ControlQuality.Hardware"/>, or at <see cref="ControlQuality.Software"/>
    /// when Windows opened a software encoder. Throws when capture or encoder cannot start.
    /// </summary>
    internal static ControlVideo Open(Func<byte[], CancellationToken, ValueTask> send, Func<uint> nextId, Action<string> log)
    {
        var preset = ControlQuality.Hardware;
        var source = BuildSource(preset, log);
        if (!source.UsingHardwareEncoder)
        {
            log($"No hardware encoder could be opened; building again for {ControlQuality.Software.Label}.");
            source.Dispose();
            preset = ControlQuality.Software;
            source = BuildSource(preset, log);
        }

        return new ControlVideo(source, preset.Fps, send, nextId, TimeProvider.System);
    }

    private static CaptureEncodeSource BuildSource(QualityPreset preset, Action<string> log)
    {
        var capture = new DxgiScreenCapture();

        // The source takes the capture over and releases it if it cannot start.
        return new CaptureEncodeSource(
            capture,
            preset,
            Sharpness.Standard,
            (size, bitsPerSecond) =>
            {
                var encoder = new MediaFoundationH264Encoder(capture.Device, size.Width, size.Height, preset.Fps, bitsPerSecond);
                log($"Encoder: {encoder.EncoderName}, {(encoder.UsingHardwareEncoder ? "hardware" : "software")}, {size.Width}x{size.Height}, {preset.Fps} fps, {bitsPerSecond} bit/s. {encoder.Configuration}");
                return encoder;
            });
    }
}
