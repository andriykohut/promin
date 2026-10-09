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
        try
        {
            HardwareEncoderAvailable = MediaFoundationH264Encoder.IsHardwareEncoderAvailable();
        }
        catch (Exception failure)
        {
            // A probe that cannot even run means no hardware encoder; the first start learns the truth.
            Trace.TraceError("The hardware encoder probe failed: {0}", failure);
        }
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

        // From here a start ends running, or with everything it built released and nothing left set.
        try
        {
            var reached = await Task.WhenAll(pcs.Select(pc => TryAddAsync(running, pc, ct))).ConfigureAwait(false);
            running.Session.Start();
            return pcs.Where((_, i) => !reached[i]).Select(pc => pc.DeviceId).ToList();
        }
        catch
        {
            if (Release(running))
            {
                await TearDownAsync(running).ConfigureAwait(false);
            }

            throw;
        }
    }

    public Task<bool> AddAsync(RoomMember pc, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(pc);

        // On the pool, as the start does it: setting up a connection is not for the UI thread to wait on.
        return Current is { } running ? Task.Run(() => TryAddAsync(running, pc, ct)) : Task.FromResult(false);
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
        if (Current is { } running && Release(running))
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

    /// <summary>Clears <paramref name="running"/> as the current broadcast. False when it already was not (someone else is releasing it).</summary>
    private bool Release(Running running)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_running, running))
            {
                return false;
            }

            _running = null;
            return true;
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

            // The connector's handshake admits only the certificate pinned for this PC. A channel that does not
            // say so (the pin was removed while it connected) is not told to show anything.
            if (channel.PinnedDeviceId != pc.DeviceId)
            {
                throw new InvalidOperationException("The connection is not pinned to the PC that was dialled.");
            }

            control = await channel.OpenControlChannelAsync(deadline.Token).ConfigureAwait(false);
            var size = running.Source.EncodeSize;
            await control.SendAsync(new StartBroadcast(size.Width, size.Height, running.Fps), deadline.Token).ConfigureAwait(false);

            // The PC answers once its display can take pictures; only then is it given a keyframe (AddClient asks
            // the source for one). This is the only read of the control stream on this side, within the connect
            // deadline; a PC whose display did not come up answers nothing and is unreachable like any other.
            if (await control.ReceiveAsync(deadline.Token).ConfigureAwait(false) is not BroadcastReady)
            {
                throw new InvalidOperationException("The PC answered the start with something other than BroadcastReady.");
            }

            var peer = new Peer(channel, control);
            Peer? replaced;
            lock (_gate)
            {
                // Teardown reads Peers only after Release cleared _running under this lock. So a peer registered
                // here is one teardown says goodbye to; a PC that answers after that is refused, and the catch
                // below closes its connection instead of leaving its display up with nothing to show.
                if (!ReferenceEquals(_running, running))
                {
                    throw new InvalidOperationException("The broadcast ended while the PC was connecting.");
                }

                running.Peers.TryGetValue(pc.DeviceId, out replaced);
                running.Peers[pc.DeviceId] = peer;
            }

            if (replaced is not null)
            {
                // The agent keeps the newest connection from this PC; the old one is just closed.
                _ = CloseAsync(replaced);
            }

            // Outside the lock: replacing a client can wait on its send loop.
            running.Session.AddClient(pc.DeviceId, new QuicVideoSink(channel));
            return true;
        }
        catch (Exception failure)
        {
            await CloseAsync(control, channel).ConfigureAwait(false);
            if (ct.IsCancellationRequested)
            {
                throw;
            }

            Trace.TraceInformation("Could not reach {0} at {1}:{2}: {3}", pc.DeviceId, pc.LastAddress, pc.Port, failure.Message);
            return false;
        }
    }

    private void OnClientDisconnected(Running running, string deviceId)
    {
        if (running.Peers.TryRemove(deviceId, out var peer))
        {
            _ = CloseAsync(peer);
        }

        // A late report from a broadcast that has ended says nothing about the PCs of the one running now.
        if (ReferenceEquals(Current, running))
        {
            ClientLost?.Invoke(deviceId);
        }
    }

    private void OnSourceFailed(Running running, Exception failure)
    {
        if (!Release(running))
        {
            return;
        }

        Trace.TraceError("The broadcast's picture source failed: {0}", failure);
        _ = TearDownAsync(running);
        BroadcastFailed?.Invoke();
    }

    /// <summary>The display changed size: every connected PC is told the new encoded size over the same connection, all at once.</summary>
    private static Task ResendStartAsync(Running running, PixelSize size) =>
        Task.WhenAll(running.Peers.Values.Select(peer => TellAsync(peer, new StartBroadcast(size.Width, size.Height, running.Fps))));

    /// <summary>Stops the session, says goodbye to every PC together, then releases the encoder and the capture, in that order.</summary>
    private static async Task TearDownAsync(Running running)
    {
        try
        {
            await Task.Run(running.Session.Stop).ConfigureAwait(false);
            await Task.WhenAll(running.Peers.Values.Select(SayGoodbyeAsync)).ConfigureAwait(false);
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
        await TellAsync(peer, new StopBroadcast()).ConfigureAwait(false);
        await CloseAsync(peer).ConfigureAwait(false);
    }

    /// <summary>Sends one control message within a second. A PC that is gone or slow is left to the session, which reports it lost when its next frame fails.</summary>
    private static async Task TellAsync(Peer peer, ControlMessage message)
    {
        try
        {
            using var deadline = new CancellationTokenSource(GoodbyeTimeout);
            await peer.Control.SendAsync(message, deadline.Token).ConfigureAwait(false);
        }
        catch (Exception failure)
        {
            Trace.TraceInformation("Could not tell a PC {0}: {1}", message.GetType().Name, failure.Message);
        }
    }

    private static Task CloseAsync(Peer peer) => CloseAsync(peer.Control, peer.Channel);

    /// <summary>Best effort: closing is how a PC is told its connection is over, and nothing here may throw.</summary>
    private static async Task CloseAsync(QuicControlChannel? control, QuicChannel? channel)
    {
        try
        {
            if (control is not null)
            {
                await control.DisposeAsync().ConfigureAwait(false);
            }

            if (channel is not null)
            {
                await channel.DisposeAsync().ConfigureAwait(false);
            }
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
