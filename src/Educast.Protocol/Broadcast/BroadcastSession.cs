using System.Collections.Concurrent;
using System.Threading.Channels;
using Educast.Protocol.Video;

namespace Educast.Protocol.Broadcast;

/// <summary>
/// The teacher-side fan-out: pulls encoded frames from one <see cref="IEncodedFrameSource"/> on a steady beat
/// and offers each frame, framed exactly once, to every connected client over that client's own
/// <see cref="IVideoSink"/>, so that one slow or dead PC never holds up the others.
/// </summary>
/// <remarks>
/// <para>
/// Backpressure is per client. Each client has a small bounded queue of whole frames and its own send loop.
/// When the queue is full the oldest frame is dropped and the frame still in flight is aborted: it is older
/// than everything behind it, so finishing it would only push the client further behind. Each frame's send
/// also has a deadline of <c>max(200 ms, 2 x frameIntervalMs)</c>; on expiry the frame is aborted and
/// counted as dropped, and the client carries on with the next one. A deadline is not a disconnect.
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
    // How long Stop/RemoveClient wait for a loop to finish after cancelling it. A sink that honours its
    // cancellation token finishes at once; this only bounds the damage from one that does not.
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(2);

    // True while the current thread is inside an event handler of some session. A handler runs on the very
    // task Stop/RemoveClient would otherwise wait for, so it must not wait for it.
    [ThreadStatic]
    private static bool t_inEventHandler;

    private sealed class ClientState
    {
        public ClientState(IVideoSink sink, int queueCapacity)
        {
            Sink = sink;

            // DropOldest plus the itemDropped callback gives exact drop accounting for free. The callback
            // runs on the capture thread that enqueued the newer frame, so it stays a counter increment and
            // an asynchronous cancellation.
            Queue = Channel.CreateBounded<byte[]>(
                new BoundedChannelOptions(queueCapacity)
                {
                    FullMode = BoundedChannelFullMode.DropOldest,
                    SingleReader = true,
                },
                _ =>
                {
                    Interlocked.Increment(ref DroppedFrameCount);

                    // The frame in flight is older than everything queued behind it: finishing it would only
                    // push this client further behind.
                    AbortInFlightFrame();
                });
        }

        public IVideoSink Sink { get; }

        public CancellationTokenSource Cts { get; } = new();

        public Channel<byte[]> Queue { get; }

        public Task SendLoop { get; set; } = Task.CompletedTask;

        // A decoder cannot start from anything but a keyframe, so a client is not offered frames until the
        // first keyframe. Only the capture loop reads or writes this.
        public bool AwaitingKeyframe { get; set; } = true;

        public CancellationTokenSource? InFlightFrameCts;
        public int DroppedFrameCount;
        public int FramesOffered;

        /// <summary>
        /// Abandons the frame stream currently being written to this client. Cancelled asynchronously: the
        /// sink's cancellation callbacks (QUIC stream aborts) must not run on the capture thread.
        /// </summary>
        public void AbortInFlightFrame()
        {
            var cts = Interlocked.Exchange(ref InFlightFrameCts, null);
            try
            {
                _ = cts?.CancelAsync();
            }
            catch (ObjectDisposedException)
            {
                // the frame finished sending between the read and the cancel
            }
        }

        public void Retire()
        {
            try
            {
                Cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            Queue.Writer.TryComplete();
        }
    }

    private readonly IEncodedFrameSource _source;
    private readonly int _frameIntervalMs;
    private readonly int _perClientQueueCapacity;
    private readonly TimeSpan _keyframeInterval;
    private readonly TimeSpan _sendDeadline;
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
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Raised when a client's send fails (for example its QUIC connection dropped), as opposed to the
    /// client being removed on purpose. The client has been removed from the session when this is raised.
    /// </summary>
    public event Action<string>? ClientDisconnected;

    /// <summary>
    /// Raised once when the frame source throws. The session has stopped (as after <see cref="Stop"/>) when
    /// this is raised; <see cref="Start"/> may be called again.
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
    /// Frames that were offered to this client but never arrived: evicted from its queue, aborted while being
    /// sent, or cancelled at their send deadline. 0 for an unknown client.
    /// </summary>
    public int DroppedFrameCountFor(string deviceId) =>
        _clients.TryGetValue(deviceId, out var state) ? Volatile.Read(ref state.DroppedFrameCount) : 0;

    /// <summary>
    /// How many frames were queued for this client since it was added, whether they were later sent or
    /// dropped. 0 for an unknown client.
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
    /// ended (a sink that ignores its cancellation token is waited for at most two seconds).
    /// </summary>
    public void Stop() => StopCore(waitForCaptureLoop: true, onlyIfRunning: null);

    /// <param name="onlyIfRunning">
    /// When given, do nothing unless that loop is still the session's current one: a loop that failed late must
    /// not stop a broadcast that was stopped and started again in the meantime.
    /// </param>
    private void StopCore(bool waitForCaptureLoop, CancellationTokenSource? onlyIfRunning)
    {
        CancellationTokenSource? cts;
        Task? loop;
        ClientState[] clients;
        lock (_gate)
        {
            if (onlyIfRunning is not null && !ReferenceEquals(_captureCts, onlyIfRunning))
            {
                return;
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

        if (waitForCaptureLoop)
        {
            WaitBounded(loop);
        }

        WaitBounded(clients.Select(c => c.SendLoop));
    }

    private static void RetireAndDrain(IReadOnlyCollection<ClientState> clients)
    {
        foreach (var client in clients)
        {
            client.Retire();
        }

        WaitBounded(clients.Select(c => c.SendLoop));
    }

    private static void WaitBounded(Task? task)
    {
        if (task is not null)
        {
            WaitBounded([task]);
        }
    }

    private static void WaitBounded(IEnumerable<Task> tasks)
    {
        if (t_inEventHandler)
        {
            return;
        }

        try
        {
            Task.WaitAll(tasks.ToArray(), DrainTimeout);
        }
        catch (AggregateException)
        {
            // A handler of ours threw on that task; nothing here to wait for any more.
        }
    }

    private static void Raise<T>(Action<T>? handler, T argument)
    {
        if (handler is null)
        {
            return;
        }

        t_inEventHandler = true;
        try
        {
            handler(argument);
        }
        finally
        {
            t_inEventHandler = false;
        }
    }

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
        catch (Exception ex)
        {
            failure = ex;
        }

        if (failure is not null)
        {
            // The session behaves as stopped before anyone is told it has failed.
            StopCore(waitForCaptureLoop: false, onlyIfRunning: loopCts);
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

        foreach (var client in _clients.Values)
        {
            if (client.AwaitingKeyframe)
            {
                if (!frame.IsKeyframe)
                {
                    continue;
                }

                client.AwaitingKeyframe = false;
            }

            Offer(client, payload);
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
        try
        {
            await foreach (var frame in state.Queue.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                using var deadline = new CancellationTokenSource(_sendDeadline, _timeProvider);
                using var frameCts = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
                Interlocked.Exchange(ref state.InFlightFrameCts, frameCts);
                try
                {
                    await state.Sink.SendFrameAsync(frame, frameCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested && frameCts.IsCancellationRequested)
                {
                    // This frame's stream was aborted: its deadline passed, or the client fell behind and a
                    // newer frame displaced it. Either way it never arrived. Carry on with the next frame.
                    Interlocked.Increment(ref state.DroppedFrameCount);
                }
                finally
                {
                    Interlocked.CompareExchange(ref state.InFlightFrameCts, null, frameCts);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // RemoveClient or Stop.
        }
        catch (Exception)
        {
            // The sink failed for a reason that is not ours (its connection dropped): this client is gone.
            disconnected = true;
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
