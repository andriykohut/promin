using System.Collections.Concurrent;
using System.Threading.Channels;
using Promin.Protocol.Video;

namespace Promin.Protocol.Broadcast;

/// <summary>
/// The teacher-side fan-out: pulls encoded frames from one <see cref="IEncodedFrameSource"/> on a steady beat
/// and offers each frame, framed exactly once, to every connected client over that client's own
/// <see cref="IVideoSink"/>, so that one slow or dead PC never holds up the others.
/// </summary>
/// <remarks>
/// <para>
/// Backpressure is per client. Each client has a small bounded queue of whole frames and its own send loop.
/// When the queue is full the oldest frame is dropped and the frame still in flight is aborted if it is a delta: it is older
/// than everything behind it, so finishing it would only push the client further behind. Each frame's send
/// also has a deadline of <c>max(200 ms, 2 x frameIntervalMs)</c> (a keyframe, the largest frame, gets
/// <c>max(that, keyframeIntervalMs / 2)</c>); on expiry the frame is aborted and counted as dropped. A
/// deadline is not a disconnect. A keyframe in flight is ended only by its own deadline (or the client's
/// removal): the queue filling up behind it never aborts it, so a PC too slow for the stream still gets a
/// fresh still picture every keyframe interval instead of nothing.
/// </para>
/// <para>
/// A frame the session cancelled (deadline or eviction) is a drop however the sink then behaves: it may
/// return normally, throw <see cref="OperationCanceledException"/> or throw anything else, and the session
/// cannot know the frame arrived. Only a send that fails when the session had not cancelled it is a
/// disconnect.
/// </para>
/// <para>
/// A client that lost a frame cannot decode the deltas that follow it. The send loop decides what may be
/// sent, from its own state alone: a keyframe always; a delta only if the frame it last delivered to this
/// client has the id just before it. Frame ids rise by one per frame produced, so a lost frame leaves a gap
/// that only a keyframe can cross; anything else queued is discarded unsent and counted as dropped. The
/// client holds its last good picture until the next keyframe the session produces anyway. No keyframe is
/// ever forced because of a drop, since that would make one bad PC cost the other nine a full frame each.
/// As an optimisation the capture loop also stops queueing deltas for such a client; those withheld deltas
/// are neither offered nor dropped.
/// </para>
/// <para>
/// The capture loop hands frames to clients with a non-blocking <c>TryWrite</c> and never awaits a sink.
/// </para>
/// <para>
/// Events are raised on the session's own threads, never on the caller's. A handler may call
/// <see cref="Stop"/>, <see cref="RemoveClient"/> or <see cref="AddClient"/>. An exception escaping a handler
/// faults the session task that raised it and is not observed by the session.
/// </para>
/// </remarks>
public sealed class BroadcastSession
{
    // How long Stop/RemoveClient wait, in total, for the loops to finish after cancelling them. A sink that
    // honours its cancellation token finishes at once; this only bounds the damage from one that does not.
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(2);

    private sealed record InFlightSend(CancellationTokenSource Cts, bool IsKeyframe);

    private sealed class ClientState
    {
        public ClientState(IVideoSink sink, int queueCapacity)
        {
            Sink = sink;

            // DropOldest plus the itemDropped callback gives exact drop accounting for free. The callback
            // runs on the capture thread that enqueued the newer frame, so it stays a counter increment, a
            // monotonic-maximum update and an asynchronous cancellation of a delta in flight.
            Queue = Channel.CreateBounded<byte[]>(
                new BoundedChannelOptions(queueCapacity)
                {
                    FullMode = BoundedChannelFullMode.DropOldest,
                    SingleReader = true,
                },
                evicted =>
                {
                    Interlocked.Increment(ref DroppedFrameCount);
                    NoteFrameLost(VideoFrameEnvelope.ReadFrom(evicted).FrameId);

                    // A delta in flight is older than everything queued behind it: finishing it would only
                    // push this client further behind. A keyframe in flight is left alone.
                    AbortInFlightDelta();
                });
        }

        public IVideoSink Sink { get; }

        public CancellationTokenSource Cts { get; } = new();

        public Channel<byte[]> Queue { get; }

        public Task SendLoop { get; set; } = Task.CompletedTask;

        // The capture loop's optimisation (correctness lives in the send loop): a client is not queued deltas
        // while it cannot decode them. The gate is open while the newest keyframe queued for this client is
        // newer than the newest frame known lost. Both are monotonic maxima, so there is no check-then-act
        // between the capture loop reopening and a send loop closing, whatever their order.
        private long _newestKeyframeQueuedId = -1;
        private long _newestLostFrameId = -1;

        public InFlightSend? InFlight;
        public int DroppedFrameCount;
        public int FramesOffered;

        public bool KeyframeGateOpen =>
            Volatile.Read(ref _newestKeyframeQueuedId) > Volatile.Read(ref _newestLostFrameId);

        public void NoteKeyframeQueued(uint frameId) => RaiseTo(ref _newestKeyframeQueuedId, frameId);

        public void NoteFrameLost(uint frameId) => RaiseTo(ref _newestLostFrameId, frameId);

        private static void RaiseTo(ref long field, long value)
        {
            long seen;
            while ((seen = Volatile.Read(ref field)) < value
                && Interlocked.CompareExchange(ref field, value, seen) != seen)
            {
            }
        }

        /// <summary>
        /// Abandons the frame stream currently being written to this client, if it is a delta. A keyframe is
        /// ended only by its own deadline: it is the one frame this client needs, and a slow client must be
        /// able to finish it however far the queue behind it overflows. Cancelled asynchronously: the sink's
        /// cancellation callbacks (QUIC stream aborts) must not run on the capture thread.
        /// </summary>
        public void AbortInFlightDelta()
        {
            var inFlight = Volatile.Read(ref InFlight);
            if (inFlight is null || inFlight.IsKeyframe
                || !ReferenceEquals(Interlocked.CompareExchange(ref InFlight, null, inFlight), inFlight))
            {
                return;
            }

            try
            {
                _ = inFlight.Cts.CancelAsync();
            }
            catch (ObjectDisposedException)
            {
                // the frame finished sending between the read and the cancel
            }
        }

        public void Retire()
        {
            Cts.Cancel();
            Queue.Writer.TryComplete();
        }
    }

    /// <summary>
    /// Test seam: when false the capture loop queues every frame for every client, so a test can show that
    /// the send loop alone keeps undecodable deltas from a client.
    /// </summary>
    internal bool UseCaptureSideGate { get; init; } = true;

    private readonly IEncodedFrameSource _source;
    private readonly int _frameIntervalMs;
    private readonly int _perClientQueueCapacity;
    private readonly TimeSpan _keyframeInterval;
    private readonly TimeSpan _sendDeadline;
    private readonly TimeSpan _keyframeSendDeadline;
    private readonly TimeProvider _timeProvider;

    private readonly ConcurrentDictionary<string, ClientState> _clients = new();
    private readonly object _gate = new();

    // Guarded by _gate.
    private CancellationTokenSource? _captureCts;
    private Task? _captureLoop;

    // Touched by the capture loop only, except _keyframeOwed.
    private uint _nextFrameId;
    private long? _lastKeyframeTimestamp;
    private int _keyframeOwed;

    public BroadcastSession(
        IEncodedFrameSource source,
        int frameIntervalMs,
        int perClientQueueCapacity = 8,
        int keyframeIntervalMs = 2000,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfLessThan(frameIntervalMs, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(perClientQueueCapacity, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(keyframeIntervalMs, 1);

        _source = source;
        _frameIntervalMs = frameIntervalMs;
        _perClientQueueCapacity = perClientQueueCapacity;
        _keyframeInterval = TimeSpan.FromMilliseconds(keyframeIntervalMs);
        _sendDeadline = TimeSpan.FromMilliseconds(Math.Max(200L, 2L * frameIntervalMs));

        // A keyframe is the largest frame and the one a client most needs, and a keyframe in flight is never
        // aborted by the queue behind it, so this deadline is its whole budget. Deltas queue behind it
        // meanwhile and are lost if they overflow, but that costs no other client anything.
        _keyframeSendDeadline = TimeSpan.FromTicks(Math.Max(_sendDeadline.Ticks, _keyframeInterval.Ticks / 2));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Raised when a client's send fails (for example its QUIC connection dropped), as opposed to the
    /// client being removed on purpose. The client has been removed from the session when this is raised.
    /// </summary>
    public event Action<string>? ClientDisconnected;

    /// <summary>
    /// Raised once when the frame source throws. The session has stopped (as after <see cref="Stop"/>) when
    /// this is raised; <see cref="Start"/> may be called again. Not raised for a failure that happens while
    /// <see cref="Stop"/> is already in progress.
    /// </summary>
    public event Action<Exception>? SourceFailed;

    /// <summary>
    /// Adds a client, possibly while the session is running. The client is sent a keyframe within one frame
    /// interval, and nothing before it. An existing client with the same id is replaced.
    /// </summary>
    public void AddClient(string deviceId, IVideoSink sink)
    {
        ArgumentNullException.ThrowIfNull(deviceId);
        ArgumentNullException.ThrowIfNull(sink);

        var state = new ClientState(sink, _perClientQueueCapacity);
        ClientState? replaced;
        lock (_gate)
        {
            _clients.TryGetValue(deviceId, out replaced);
            _clients[deviceId] = state;
            state.SendLoop = Task.Run(() => RunSendLoopAsync(deviceId, state));
        }

        // The frame source is asked for a keyframe on its next call, so the new client has something it can
        // decode even when the screen is still.
        Volatile.Write(ref _keyframeOwed, 1);

        if (replaced is not null)
        {
            RetireAndDrain([replaced]);
        }
    }

    /// <summary>
    /// Stops sending to one client without disturbing the others. On return that client's sink is no longer
    /// being called (a sink that ignores its cancellation token is waited for at most two seconds).
    /// </summary>
    public void RemoveClient(string deviceId)
    {
        ClientState? state;
        lock (_gate)
        {
            _clients.TryRemove(deviceId, out state);
        }

        if (state is not null)
        {
            RetireAndDrain([state]);
        }
    }

    /// <summary>
    /// Frames that were queued for this client but never arrived: evicted from its queue, aborted while being
    /// sent, cancelled at their send deadline, or discarded unsent because an earlier frame was lost. Deltas
    /// withheld from a client whose keyframe gate is shut were never queued, so they are not counted. 0 for an
    /// unknown client.
    /// </summary>
    public int DroppedFrameCountFor(string deviceId) =>
        _clients.TryGetValue(deviceId, out var state) ? Volatile.Read(ref state.DroppedFrameCount) : 0;

    /// <summary>
    /// How many frames were queued for this client since it was added, whether they were later sent or
    /// dropped. Frames withheld because the client is waiting for a keyframe were never queued and are not
    /// counted. 0 for an unknown client.
    /// </summary>
    public int FramesOfferedTo(string deviceId) =>
        _clients.TryGetValue(deviceId, out var state) ? Volatile.Read(ref state.FramesOffered) : 0;

    /// <exception cref="InvalidOperationException">The session is already running.</exception>
    public void Start()
    {
        lock (_gate)
        {
            if (_captureLoop is not null)
            {
                throw new InvalidOperationException("The broadcast session is already running.");
            }

            _nextFrameId = 0;
            _lastKeyframeTimestamp = null;
            Volatile.Write(ref _keyframeOwed, 1);

            // Created here, not on the loop's thread, so that a tick that falls due before the loop first
            // looks at the timer is not lost.
            var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(_frameIntervalMs), _timeProvider);

            // Never disposed: a CancellationTokenSource with no timer and no wait handle holds nothing to free.
            var cts = new CancellationTokenSource();
            _captureCts = cts;
            _captureLoop = Task.Run(() => RunCaptureLoopAsync(timer, cts));
        }
    }

    /// <summary>
    /// Stops the broadcast and removes every client. Returns once the capture loop and the send loops have
    /// ended, waiting for all of them together for at most two seconds (a sink or source that ignores
    /// cancellation is what could use that up).
    /// </summary>
    public void Stop() => StopCore(waitForCaptureLoop: true, onlyIfRunning: null);

    /// <param name="onlyIfRunning">
    /// When given, do nothing unless that loop is still the session's current one: a loop that failed late must
    /// not stop a broadcast that was stopped and started again in the meantime.
    /// </param>
    /// <returns>Whether this call stopped the session.</returns>
    private bool StopCore(bool waitForCaptureLoop, CancellationTokenSource? onlyIfRunning)
    {
        CancellationTokenSource? cts;
        Task? loop;
        ClientState[] clients;
        lock (_gate)
        {
            if (onlyIfRunning is not null && !ReferenceEquals(_captureCts, onlyIfRunning))
            {
                return false;
            }

            cts = _captureCts;
            loop = _captureLoop;
            _captureCts = null;
            _captureLoop = null;
            clients = _clients.Values.ToArray();
            _clients.Clear();
        }

        cts?.Cancel();
        foreach (var client in clients)
        {
            client.Retire();
        }

        // One budget for everything, not one per wait.
        var waitingFor = clients.Select(c => c.SendLoop);
        WaitBounded(waitForCaptureLoop && loop is not null ? waitingFor.Append(loop) : waitingFor);
        return true;
    }

    private static void RetireAndDrain(IReadOnlyCollection<ClientState> clients)
    {
        foreach (var client in clients)
        {
            client.Retire();
        }

        WaitBounded(clients.Select(c => c.SendLoop));
    }

    private static void WaitBounded(IEnumerable<Task> tasks)
    {
        try
        {
            Task.WaitAll(tasks.ToArray(), DrainTimeout);
        }
        catch (AggregateException)
        {
            // A handler of ours threw on one of those tasks; there is nothing left to wait for on it.
        }
    }

    private static void Raise<T>(Action<T>? handler, T argument) => handler?.Invoke(argument);

    // ---- capture loop ----------------------------------------------------------------------------------

    private async Task RunCaptureLoopAsync(PeriodicTimer timer, CancellationTokenSource loopCts)
    {
        var ct = loopCts.Token;
        Exception? failure = null;
        try
        {
            using (timer)
            {
                do
                {
                    ct.ThrowIfCancellationRequested();
                    OfferNextFrame();
                }
                while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false));
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Stop: the only normal way out of this loop.
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            failure = ex;
        }
        catch (Exception)
        {
            // The source failed while Stop was already in progress: a deliberate stop is not a failure.
        }

        // The session behaves as stopped before anyone is told it has failed; and it is not reported at all
        // if this loop is no longer the session's current one (stopped, or stopped and started again).
        if (failure is not null && StopCore(waitForCaptureLoop: false, onlyIfRunning: loopCts))
        {
            Raise(SourceFailed, failure);
        }
    }

    private void OfferNextFrame()
    {
        // One reading of the clock per tick, taken before the source is asked, so the keyframe cadence is
        // measured on the beat rather than on how long the source or the fan-out happened to take.
        var now = _timeProvider.GetTimestamp();
        var owed = Interlocked.Exchange(ref _keyframeOwed, 0) != 0;
        var forceKeyframe = owed
            || _lastKeyframeTimestamp is not { } last
            || _timeProvider.GetElapsedTime(last, now) >= _keyframeInterval;

        // "Nothing new" (false) is a normal outcome and simply means nothing to send this tick.
        var produced = _source.TryGetNextFrame(TimeSpan.FromMilliseconds(_frameIntervalMs), forceKeyframe, out var frame);
        if (produced && frame.Data is null)
        {
            throw new InvalidOperationException("The frame source reported a frame but returned no data.");
        }

        if (produced && frame.IsKeyframe)
        {
            _lastKeyframeTimestamp = now;
        }
        else if (owed)
        {
            // The keyframe a new client is waiting for was not produced; ask again on the next tick.
            Volatile.Write(ref _keyframeOwed, 1);
        }

        if (!produced)
        {
            return;
        }

        // Envelope and encoded bytes are composed exactly once and the same buffer is handed to every
        // client: one capture, one encode, one framing pass, however many clients there are.
        var encoded = frame.Data;
        var payload = new byte[VideoFrameEnvelope.Size + encoded.Length];
        new VideoFrameEnvelope(_nextFrameId++, frame.IsKeyframe).WriteTo(payload);
        encoded.CopyTo(payload.AsSpan(VideoFrameEnvelope.Size));

        var frameId = _nextFrameId - 1;
        foreach (var client in _clients.Values)
        {
            // A client that cannot decode a delta is not queued one: not counted as offered, not as dropped.
            // (The send loop would discard it anyway; this just avoids queueing it.)
            if (UseCaptureSideGate && !client.KeyframeGateOpen && !frame.IsKeyframe)
            {
                continue;
            }

            Offer(client, payload);

            // After the write, not before: queueing this keyframe may evict an older frame, and that loss is
            // noted first, but a keyframe starts a new chain, so only a loss newer than it matters.
            if (frame.IsKeyframe)
            {
                client.NoteKeyframeQueued(frameId);
            }
        }
    }

    private static void Offer(ClientState client, byte[] payload)
    {
        // Counted before the write: a drop caused by this very write is counted inside it, and a reader must
        // never see more frames dropped than offered.
        Interlocked.Increment(ref client.FramesOffered);
        if (!client.Queue.Writer.TryWrite(payload))
        {
            // The queue is closed (the client is being removed): nothing was offered after all.
            Interlocked.Decrement(ref client.FramesOffered);
        }
    }

    // ---- send loop -------------------------------------------------------------------------------------

    private async Task RunSendLoopAsync(string deviceId, ClientState state)
    {
        var ct = state.Cts.Token;
        var disconnected = false;
        long? lastDeliveredId = null;
        try
        {
            await foreach (var frame in state.Queue.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                // ReadAllAsync keeps yielding what is already queued after cancellation; a removed client's
                // sink must not be called again.
                ct.ThrowIfCancellationRequested();

                // This loop is the judge of what the client can decode, from its own state alone: a keyframe
                // always; a delta only if the frame last delivered is the one just before it. A frame that was
                // evicted, withheld, discarded or aborted leaves a gap that only a keyframe can cross.
                var envelope = VideoFrameEnvelope.ReadFrom(frame);
                var isKeyframe = envelope.IsKeyframe;
                if (!isKeyframe && lastDeliveredId != (long)envelope.FrameId - 1)
                {
                    // Queued for this client, never sent: dropped.
                    Interlocked.Increment(ref state.DroppedFrameCount);
                    continue;
                }

                using var deadline = new CancellationTokenSource(isKeyframe ? _keyframeSendDeadline : _sendDeadline, _timeProvider);
                using var frameCts = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
                var inFlight = new InFlightSend(frameCts, isKeyframe);
                Volatile.Write(ref state.InFlight, inFlight);

                Exception? sendFailure = null;
                try
                {
                    await state.Sink.SendFrameAsync(frame, frameCts.Token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    sendFailure = ex;
                }
                finally
                {
                    Interlocked.CompareExchange(ref state.InFlight, null, inFlight);
                }

                // What happened to the frame is decided by what the session did, not by what the sink threw:
                // a real transport can return normally, or throw something other than
                // OperationCanceledException, after its stream has been aborted.
                if (ct.IsCancellationRequested)
                {
                    // RemoveClient or Stop; the next read ends the loop.
                    continue;
                }

                if (frameCts.IsCancellationRequested)
                {
                    // The session abandoned this frame (its deadline passed, or the client fell behind and a
                    // newer frame displaced it). The session cannot know it arrived, so it is a drop, however
                    // the send ended, and never a disconnect.
                    Interlocked.Increment(ref state.DroppedFrameCount);
                    state.NoteFrameLost(envelope.FrameId);
                    continue;
                }

                if (sendFailure is not null)
                {
                    // The sink failed and the session had not cancelled it: its connection dropped.
                    disconnected = true;
                    break;
                }

                lastDeliveredId = envelope.FrameId;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // RemoveClient or Stop.
        }

        // Only report it if this is still the registered client: RemoveClient, Stop or a replacing AddClient
        // may already have taken it out on purpose, and a rejoined client with the same id must not be removed.
        if (disconnected && _clients.TryRemove(new KeyValuePair<string, ClientState>(deviceId, state)))
        {
            state.Retire();
            Raise(ClientDisconnected, deviceId);
        }
    }
}
