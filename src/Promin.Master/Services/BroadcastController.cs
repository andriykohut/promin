using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Promin.Master.Core.Rooms;
using Promin.Master.Core.Wall;
using Promin.MediaPipeline.Broadcast;
using Promin.MediaPipeline.Capture;
using Promin.MediaPipeline.Encoding;
using Promin.Protocol.Broadcast;
using Promin.Protocol.Messages;
using Promin.Protocol.Transport;
using Promin.Protocol.Video;

namespace Promin.Master.Services;

/// <summary>
/// <see cref="IBroadcastController"/> over the real pipeline: one <see cref="DxgiScreenCapture"/>
/// and <see cref="MediaFoundationH264Encoder"/> behind a <see cref="CaptureEncodeSource"/>, one
/// <see cref="BroadcastSession"/>, and each agent PC's standing connection from the room connections:
/// the broadcast never dials and never closes a connection; it only starts and stops on it. Composition
/// only; every decision lives in the parts, which are tested.
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

    // How long a start waits for every PC to answer before the picture starts for those that have.
    private static readonly TimeSpan StartTogetherTime = TimeSpan.FromSeconds(1);

    private readonly RoomConnections<QuicPcConnection> _connections;
    private readonly object _gate = new();
    private Running? _running;

    // The newest teardown: what the next start, and the closing app, wait for. It never faults.
    private Task _tearDown = Task.CompletedTask;

    public BroadcastController(RoomConnections<QuicPcConnection> connections)
    {
        ArgumentNullException.ThrowIfNull(connections);
        _connections = connections;
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
        Task previous;
        lock (_gate)
        {
            if (_running is not null)
            {
                throw new InvalidOperationException("The broadcast is already running.");
            }

            previous = _tearDown;
        }

        // A start ends running, or with everything it built released and nothing left set.
        Running? built = null;
        try
        {
            // The broadcast before this one lets go of the screen first: a second capture of the same display,
            // made while the first is still alive, is refused.
            await previous.ConfigureAwait(false);

            var running = built = await Task.Run(() => Build(preset, sharpness), ct).ConfigureAwait(false);
            HardwareEncoderAvailable = running.Source.UsingHardwareEncoder;
            running.Session.ClientDisconnected += id => OnClientDisconnected(running, id);
            running.Session.SourceFailed += failure => OnSourceFailed(running, failure);
            running.Source.EncodeSizeChanged += size => _ = ResendStartAsync(running, size);
            lock (_gate)
            {
                _running = running;
            }

            // The picture starts once every PC has answered, or after a second for those that have: one PC that
            // does not answer must not keep the others on a black screen for its whole five seconds. The rest
            // are added to the running session as they answer.
            var answers = Task.WhenAll(pcs.Select(pc => TryAddAsync(running, pc, ct)));
            await Task.WhenAny(answers, Task.Delay(StartTogetherTime)).ConfigureAwait(false);
            running.Session.Start();
            var reached = await answers.ConfigureAwait(false);

            // The picture source failed, or the app is closing, while the last PCs were answering.
            if (!ReferenceEquals(Current, running))
            {
                throw new InvalidOperationException("The broadcast ended while it was starting.");
            }

            // A PC that answered and was lost again before this returns is not receiving: the caller brings it back.
            var unreachable = pcs
                .Where((pc, i) => !reached[i] || !running.Peers.ContainsKey(pc.DeviceId))
                .Select(pc => pc.DeviceId)
                .ToList();
            Trace.TraceInformation("Broadcast started: {0} of {1} PCs receiving.", pcs.Count - unreachable.Count, pcs.Count);
            return unreachable;
        }
        catch (Exception failure)
        {
            Trace.TraceError("The broadcast could not start: {0}", failure);
            if (built is not null)
            {
                End(built, "its start failed");
                await TearDown.ConfigureAwait(false);
            }

            throw;
        }
    }

    public Task<bool> AddAsync(RoomMember pc, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(pc);
        if (Current is not { } running)
        {
            return Task.FromResult(false);
        }

        // On the pool, as the start does it: setting up a connection is not for the UI thread to wait on.
        return Task.Run(async () =>
        {
            var joined = await TryAddAsync(running, pc, ct).ConfigureAwait(false);
            if (joined)
            {
                Trace.TraceInformation("{0} ({1}) joined the running broadcast.", pc.DisplayName, pc.DeviceId);
            }

            return joined;
        });
    }

    /// <summary>
    /// Stops sending to the PC and tells it to stop. A PC that cannot be told is dropped, so its window does not
    /// stay up: the PC removes it when its connection ends, and the wall dials it again.
    /// </summary>
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
                if (!await TellAsync(peer, new StopBroadcast()).ConfigureAwait(false))
                {
                    _connections.Drop(deviceId);
                }
            }
        });
    }

    public void Stop()
    {
        if (Current is { } running)
        {
            End(running, "stopped");
        }
    }

    /// <summary>
    /// Stops the broadcast and waits, for at most <paramref name="timeout"/>, until every PC has been told and
    /// the screen capture released. For closing the app: a process that exits first leaves the viewers
    /// looking at the last picture until their connection times out.
    /// </summary>
    public void StopAndWait(TimeSpan timeout)
    {
        Stop();
        TearDown.Wait(timeout);
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

    private Task TearDown
    {
        get
        {
            lock (_gate)
            {
                return _tearDown;
            }
        }
    }

    /// <summary>
    /// Ends <paramref name="running"/> if it is still the current broadcast: from here no PC is registered with
    /// it, and its teardown begins on the pool. False when it had been ended already.
    /// </summary>
    private bool End(Running running, string why)
    {
        int pcs;
        lock (_gate)
        {
            if (!ReferenceEquals(_running, running))
            {
                return false;
            }

            _running = null;
            pcs = running.Peers.Count;
            _tearDown = Task.Run(() => TearDownAsync(running));
        }

        Trace.TraceInformation("Broadcast ended ({0}); {1} PCs were receiving.", why, pcs);
        return true;
    }

    private static Running Build(QualityPreset preset, Sharpness sharpness)
    {
        var source = BuildSource(preset, sharpness);
        if (!source.UsingHardwareEncoder && preset != QualityPreset.SoftwareFallback)
        {
            // The probe promised a hardware encoder and Windows opened its software one. What runs is the
            // quality the wall then shows, not the chosen one encoded in software.
            Trace.TraceInformation("No hardware encoder could be opened; building again for {0}.", QualityPreset.SoftwareFallback.Label);
            source.Dispose();
            preset = QualityPreset.SoftwareFallback;
            source = BuildSource(preset, sharpness);
        }

        return new Running(source, new BroadcastSession(new KeyframeLog(source), 1000 / preset.Fps), preset.Fps);
    }

    private static CaptureEncodeSource BuildSource(QualityPreset preset, Sharpness sharpness)
    {
        var capture = new DxgiScreenCapture();

        // The source takes the capture over and releases it if it cannot start.
        return new CaptureEncodeSource(
            capture,
            preset,
            sharpness,
            (size, bitsPerSecond) =>
            {
                var encoder = new MediaFoundationH264Encoder(capture.Device, size.Width, size.Height, preset.Fps, bitsPerSecond);
                Trace.TraceInformation(
                    "Encoder: {0}, {1}, {2}x{3}, {4} fps, {5} bit/s. {6}",
                    encoder.EncoderName,
                    encoder.UsingHardwareEncoder ? "hardware" : "software",
                    size.Width,
                    size.Height,
                    preset.Fps,
                    bitsPerSecond,
                    encoder.Configuration);
                return encoder;
            });
    }

    /// <summary>Takes the PC's connection, says what is coming, and adds the PC to the session. False when it could not be reached, or did not answer, in time.</summary>
    private async Task<bool> TryAddAsync(Running running, RoomMember pc, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(ConnectTimeout);

        QuicPcConnection? connection = null;
        try
        {
            connection = await _connections.GetAsync(pc, deadline.Token).ConfigureAwait(false);
            if (connection is null)
            {
                Trace.TraceInformation("Could not reach {0} at {1}:{2}.", pc.DeviceId, pc.LastAddress, pc.Port);
                return false;
            }

            var control = await connection.ControlAsync(deadline.Token).ConfigureAwait(false);
            var size = running.Source.EncodeSize;

            // The PC answers once its display can take pictures; only then is it given a keyframe (AddClient asks
            // the source for one). A PC whose display did not come up answers nothing and is unreachable like any other.
            await control.StartAsync(new StartBroadcast(size.Width, size.Height, running.Fps), deadline.Token).ConfigureAwait(false);

            var peer = new Peer(connection, control, pc.DisplayName);
            lock (_gate)
            {
                // Teardown reads Peers only after End cleared _running under this lock. So a peer registered here is
                // one teardown says stop to; a PC that answers after that has its connection dropped below instead.
                if (!ReferenceEquals(_running, running))
                {
                    throw new InvalidOperationException("The broadcast ended while the PC was connecting.");
                }

                running.Peers[pc.DeviceId] = peer;
            }

            // Outside the lock: replacing a client can wait on its send loop.
            running.Session.AddClient(pc.DeviceId, new QuicVideoSink(connection.Channel));
            return true;
        }
        catch (Exception failure)
        {
            if (connection is not null)
            {
                // A PC that took the start but is not being sent to must not keep a display up; and a connection that
                // did not answer in time is not trusted with the next start (its late answer would be read as it).
                _connections.Drop(pc.DeviceId);
            }

            if (ct.IsCancellationRequested)
            {
                throw;
            }

            Trace.TraceInformation("Could not start the broadcast on {0} at {1}:{2}: {3}", pc.DeviceId, pc.LastAddress, pc.Port, failure.Message);
            return false;
        }
    }

    private void OnClientDisconnected(Running running, string deviceId)
    {
        if (running.Peers.TryRemove(deviceId, out var peer))
        {
            // A send failed: the connection is dead, and dropping it gets it dialled again.
            _connections.Drop(deviceId);
        }

        // A late report from a broadcast that has ended says nothing about the PCs of the one running now.
        if (ReferenceEquals(Current, running))
        {
            Trace.TraceInformation("Lost {0}.", peer is null ? deviceId : $"{peer.Name} ({deviceId})");
            ClientLost?.Invoke(deviceId);
        }
    }

    private void OnSourceFailed(Running running, Exception failure)
    {
        if (!End(running, "its picture source failed"))
        {
            return;
        }

        Trace.TraceError("The broadcast's picture source failed: {0}", failure);
        BroadcastFailed?.Invoke();
    }

    /// <summary>The display changed size: every connected PC is told the new encoded size over the same connection, all at once.</summary>
    private static Task ResendStartAsync(Running running, PixelSize size) =>
        Task.WhenAll(running.Peers.Values.Select(peer => TellAsync(peer, new StartBroadcast(size.Width, size.Height, running.Fps))));

    /// <summary>
    /// Tells every PC the broadcast is over, all together and within a second; stops the session; releases the
    /// encoder and the capture. The connections stay open, so the screen pictures go on, except those of PCs that
    /// could not be told: they are dropped, so their window does not stay up.
    /// Runs on the pool, after <see cref="End"/>, so the set of PCs no longer grows. Never throws.
    /// </summary>
    private async Task TearDownAsync(Running running)
    {
        try
        {
            try
            {
                await Task.WhenAll(running.Peers.Select(async entry =>
                {
                    if (!await TellAsync(entry.Value, new StopBroadcast()).ConfigureAwait(false))
                    {
                        _connections.Drop(entry.Key);
                    }
                })).ConfigureAwait(false);
                running.Session.Stop();
            }
            finally
            {
                running.Source.Dispose();
                running.Peers.Clear();
            }
        }
        catch (Exception failure)
        {
            Trace.TraceError("Stopping the broadcast failed: {0}", failure);
        }
    }

    /// <summary>
    /// Sends one control message within a second. True when it was sent; false when the PC is gone or slow, which
    /// the session also reports as lost when its next frame fails.
    /// </summary>
    private static async Task<bool> TellAsync(Peer peer, ControlMessage message)
    {
        try
        {
            using var deadline = new CancellationTokenSource(GoodbyeTimeout);
            await peer.Control.SendAsync(message, deadline.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception failure)
        {
            Trace.TraceInformation("Could not tell a PC {0}: {1}", message.GetType().Name, failure.Message);
            return false;
        }
    }

    private sealed class Running(CaptureEncodeSource source, BroadcastSession session, int fps)
    {
        public CaptureEncodeSource Source { get; } = source;

        public BroadcastSession Session { get; } = session;

        public int Fps { get; } = fps;

        public ConcurrentDictionary<string, Peer> Peers { get; } = new(StringComparer.Ordinal);
    }

    /// <param name="Name">The PC's name in the room, for the log.</param>
    private sealed record Peer(QuicPcConnection Connection, ConsoleControl Control, string Name);

    /// <summary>
    /// Writes the size of every keyframe the source produces to the log, about one every two seconds: it is
    /// how a still picture that looks soft is diagnosed afterwards.
    /// </summary>
    private sealed class KeyframeLog(IEncodedFrameSource source) : IEncodedFrameSource
    {
        public bool TryGetNextFrame(TimeSpan timeout, bool forceKeyframe, out EncodedVideoFrame frame)
        {
            var produced = source.TryGetNextFrame(timeout, forceKeyframe, out frame);
            if (produced && frame.IsKeyframe)
            {
                Trace.TraceInformation("Keyframe: {0} bytes.", frame.Data.Length);
            }

            return produced;
        }
    }

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
