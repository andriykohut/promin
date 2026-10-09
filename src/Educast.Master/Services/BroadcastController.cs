using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using Educast.Master.Core.Rooms;
using Educast.Master.Core.Wall;
using Educast.MediaPipeline.Broadcast;
using Educast.MediaPipeline.Capture;
using Educast.MediaPipeline.Encoding;
using Educast.Protocol.Broadcast;
using Educast.Protocol.Messages;
using Educast.Protocol.Transport;
using Educast.Protocol.Video;

namespace Educast.Master.Services;

/// <summary>
/// <see cref="IBroadcastController"/> over the real pipeline: one <see cref="DxgiScreenCapture"/>
/// and <see cref="MediaFoundationH264Encoder"/> behind a <see cref="CaptureEncodeSource"/>, one
/// <see cref="BroadcastSession"/>, and one QUIC connection per student PC. Composition only; every
/// decision lives in the parts, which are tested.
/// </summary>
/// <remarks>
/// <b>Threads.</b> <see cref="StartAsync"/>, <see cref="AddAsync"/>, <see cref="StopSendingTo"/>,
/// <see cref="Stop"/> and <see cref="StatsFor"/> are called on the UI thread; the pipeline is built
/// and torn down on the thread pool so the UI never waits on a driver. <see cref="ClientLost"/> and
/// <see cref="BroadcastFailed"/> are raised on the session's threads. A size change arrives on the
/// capture thread and re-sends <see cref="StartBroadcast"/> from there without waiting.
/// </remarks>
public sealed class BroadcastController : IBroadcastController
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan GoodbyeTimeout = TimeSpan.FromSeconds(1);

    private readonly QuicMasterConnector _connector;
    private readonly object _gate = new();
    private Running? _running;

    public BroadcastController(QuicMasterConnector connector)
    {
        ArgumentNullException.ThrowIfNull(connector);
        _connector = connector;
        HardwareEncoderAvailable = MediaFoundationH264Encoder.IsHardwareEncoderAvailable();
    }

    public bool HardwareEncoderAvailable { get; private set; }

    /// <summary>The primary display in physical pixels (the process is per-monitor DPI aware), what the capture sees.</summary>
    public PixelSize DisplaySize => new(GetSystemMetrics(SM_CXSCREEN), GetSystemMetrics(SM_CYSCREEN));

    public event Action<string>? ClientLost;

    public event Action? BroadcastFailed;

    public async Task<IReadOnlyCollection<string>> StartAsync(
        IReadOnlyCollection<RoomMember> pcs, QualityPreset preset, Sharpness sharpness, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(pcs);
        ArgumentNullException.ThrowIfNull(preset);
        lock (_gate)
        {
            if (_running is not null)
            {
                throw new InvalidOperationException("The broadcast is already running.");
            }
        }

        var running = await Task.Run(() => Build(preset, sharpness), ct).ConfigureAwait(false);
        HardwareEncoderAvailable = running.Source.UsingHardwareEncoder;
        running.Session.ClientDisconnected += id => OnClientDisconnected(running, id);
        running.Session.SourceFailed += failure => OnSourceFailed(running, failure);
        running.Source.EncodeSizeChanged += size => _ = ResendStartAsync(running, size);
        lock (_gate)
        {
            _running = running;
        }

        var reached = await Task.WhenAll(pcs.Select(pc => TryAddAsync(running, pc, ct))).ConfigureAwait(false);
        running.Session.Start();
        return pcs.Where((_, i) => !reached[i]).Select(pc => pc.DeviceId).ToList();
    }

    public Task<bool> AddAsync(RoomMember pc, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(pc);
        return Current is { } running ? TryAddAsync(running, pc, ct) : Task.FromResult(false);
    }

    public void StopSendingTo(string deviceId)
    {
        if (Current is not { } running)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            running.Session.RemoveClient(deviceId);
            if (running.Peers.TryRemove(deviceId, out var peer))
            {
                await SayGoodbyeAsync(peer).ConfigureAwait(false);
            }
        });
    }

    public void Stop()
    {
        Running? running;
        lock (_gate)
        {
            running = _running;
            _running = null;
        }

        if (running is not null)
        {
            _ = TearDownAsync(running);
        }
    }

    public BroadcastStats StatsFor(string deviceId) =>
        Current is { } running
            ? new BroadcastStats(running.Session.DroppedFrameCountFor(deviceId), running.Session.FramesOfferedTo(deviceId))
            : default;

    private Running? Current
    {
        get
        {
            lock (_gate)
            {
                return _running;
            }
        }
    }

    private static Running Build(QualityPreset preset, Sharpness sharpness)
    {
        var capture = new DxgiScreenCapture();

        // The source takes the capture over and releases it if it cannot start.
        var source = new CaptureEncodeSource(
            capture,
            preset,
            sharpness,
            (size, bitsPerSecond) => new MediaFoundationH264Encoder(capture.Device, size.Width, size.Height, preset.Fps, bitsPerSecond));
        return new Running(source, new BroadcastSession(source, 1000 / preset.Fps), preset.Fps);
    }

    /// <summary>Connects, says what is coming, and adds the PC to the session. False, and nothing left open, when it could not be reached in time.</summary>
    private async Task<bool> TryAddAsync(Running running, RoomMember pc, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(ConnectTimeout);

        QuicChannel? channel = null;
        QuicControlChannel? control = null;
        try
        {
            var endpoint = new IPEndPoint(IPAddress.Parse(pc.LastAddress), pc.Port);
            channel = await _connector.ConnectAsync(endpoint, pc.DeviceId, deadline.Token).ConfigureAwait(false);
            control = await channel.OpenControlChannelAsync(deadline.Token).ConfigureAwait(false);
            var size = running.Source.EncodeSize;
            await control.SendAsync(new StartBroadcast(size.Width, size.Height, running.Fps), deadline.Token).ConfigureAwait(false);

            var peer = new Peer(channel, control);
            if (running.Peers.TryGetValue(pc.DeviceId, out var replaced))
            {
                // The agent keeps the newest connection from this PC; the old one is just closed.
                _ = CloseAsync(replaced);
            }

            running.Peers[pc.DeviceId] = peer;
            running.Session.AddClient(pc.DeviceId, new QuicVideoSink(channel));
            return true;
        }
        catch (Exception failure) when (!ct.IsCancellationRequested)
        {
            Trace.TraceInformation("Could not reach {0} at {1}:{2}: {3}", pc.DeviceId, pc.LastAddress, pc.Port, failure.Message);
            if (control is not null)
            {
                await control.DisposeAsync().ConfigureAwait(false);
            }

            if (channel is not null)
            {
                await channel.DisposeAsync().ConfigureAwait(false);
            }

            return false;
        }
    }

    private void OnClientDisconnected(Running running, string deviceId)
    {
        if (running.Peers.TryRemove(deviceId, out var peer))
        {
            _ = CloseAsync(peer);
        }

        ClientLost?.Invoke(deviceId);
    }

    private void OnSourceFailed(Running running, Exception failure)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_running, running))
            {
                return;
            }

            _running = null;
        }

        Trace.TraceError("The broadcast's picture source failed: {0}", failure);
        _ = TearDownAsync(running);
        BroadcastFailed?.Invoke();
    }

    /// <summary>The display changed size: every connected PC is told the new encoded size over the same connection.</summary>
    private static async Task ResendStartAsync(Running running, PixelSize size)
    {
        foreach (var peer in running.Peers.Values)
        {
            try
            {
                using var deadline = new CancellationTokenSource(GoodbyeTimeout);
                await peer.Control.SendAsync(new StartBroadcast(size.Width, size.Height, running.Fps), deadline.Token).ConfigureAwait(false);
            }
            catch (Exception failure)
            {
                // The session reports the PC lost when its next frame fails; nothing more to do here.
                Trace.TraceInformation("Could not tell a PC about the new picture size: {0}", failure.Message);
            }
        }
    }

    /// <summary>Stops the session, says goodbye to every PC, then releases the encoder and the capture, in that order.</summary>
    private static async Task TearDownAsync(Running running)
    {
        try
        {
            await Task.Run(running.Session.Stop).ConfigureAwait(false);
            foreach (var peer in running.Peers.Values)
            {
                await SayGoodbyeAsync(peer).ConfigureAwait(false);
            }

            running.Peers.Clear();
            running.Source.Dispose();
        }
        catch (Exception failure)
        {
            Trace.TraceError("Stopping the broadcast failed: {0}", failure);
        }
    }

    private static async Task SayGoodbyeAsync(Peer peer)
    {
        try
        {
            using var deadline = new CancellationTokenSource(GoodbyeTimeout);
            await peer.Control.SendAsync(new StopBroadcast(), deadline.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The PC is gone or slow; closing the connection tells it the same thing.
        }

        await CloseAsync(peer).ConfigureAwait(false);
    }

    private static async Task CloseAsync(Peer peer)
    {
        try
        {
            await peer.Control.DisposeAsync().ConfigureAwait(false);
            await peer.Channel.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception failure)
        {
            Trace.TraceInformation("Closing a PC's connection failed: {0}", failure.Message);
        }
    }

    private sealed class Running(CaptureEncodeSource source, BroadcastSession session, int fps)
    {
        public CaptureEncodeSource Source { get; } = source;

        public BroadcastSession Session { get; } = session;

        public int Fps { get; } = fps;

        public ConcurrentDictionary<string, Peer> Peers { get; } = new(StringComparer.Ordinal);
    }

    private sealed record Peer(QuicChannel Channel, QuicControlChannel Control);

    /// <summary>
    /// One PC's frames over its <see cref="QuicChannel"/>. The transport does not throw for a
    /// cancellation that lands as the send completes; the sink's contract requires it.
    /// </summary>
    private sealed class QuicVideoSink(QuicChannel channel) : IVideoSink
    {
        public async ValueTask SendFrameAsync(byte[] envelopeAndFrame, CancellationToken ct)
        {
            await channel.SendVideoFrameAsync(envelopeAndFrame, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
        }
    }

    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);
}
