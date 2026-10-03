using System.Diagnostics;
using Educast.Protocol.Broadcast;
using Educast.Protocol.Tests.TestDoubles;
using Educast.Protocol.Video;

namespace Educast.Protocol.Tests.Broadcast;

/// <summary>
/// These tests are concurrent by nature, so none of them sleeps a fixed time and hopes: each waits on
/// a condition with a generous timeout. Where an interval matters (keyframe cadence, send deadline),
/// the session runs on a <see cref="ManualTimeProvider"/> and the test moves the clock itself.
/// </summary>
public class BroadcastSessionTests
{
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(30);

    // Real-time tests never want a keyframe interval to elapse by accident on a slow machine.
    private const int NeverMs = 60_000;

    private static async Task Eventually(Func<bool> condition, string what)
    {
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            if (clock.Elapsed > WaitLimit)
            {
                Assert.Fail($"Timed out after {WaitLimit.TotalSeconds:0}s waiting for: {what}");
            }

            await Task.Delay(1);
        }
    }

    /// <summary>Moves the manual clock by one frame interval and waits for the loop to take that tick.</summary>
    private static async Task TickAsync(FakeFrameSource source, ManualTimeProvider clock, int ms, RecordingVideoSink? keepUpWith = null)
    {
        var callsBefore = source.CallCount;
        clock.Advance(TimeSpan.FromMilliseconds(ms));
        await Eventually(() => source.CallCount > callsBefore, "the capture loop to take its next tick");

        // Fake ticks come far faster than real ones. A client that is meant to be keeping up is given the
        // time to, or its queue would legitimately overflow and the test would be measuring the test.
        if (keepUpWith is not null)
        {
            await Eventually(() => keepUpWith.ReceivedFrames.Count == source.ProducedCount, "the client to keep up with the beat");
        }
    }

    private static VideoFrameEnvelope EnvelopeOf(byte[] frame) => VideoFrameEnvelope.ReadFrom(frame);

    private static byte[] PayloadOf(byte[] frame) => frame[VideoFrameEnvelope.Size..];

    // ---- fan-out ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Start_AsksTheSourceOncePerFrame_AndDeliversTheSameFrameToEveryClient()
    {
        var source = new FakeFrameSource(SourceStep.NothingNew, SourceStep.Changed, SourceStep.Changed, SourceStep.Changed);
        var session = new BroadcastSession(source, frameIntervalMs: 1, keyframeIntervalMs: NeverMs);
        var clients = new[] { new RecordingVideoSink(), new RecordingVideoSink(), new RecordingVideoSink() };
        for (var i = 0; i < clients.Length; i++)
        {
            session.AddClient($"client-{i}", clients[i]);
        }

        session.Start();
        await Eventually(() => clients.All(c => c.ReceivedFrames.Count == 3), "all three clients to receive three frames");
        session.Stop();

        // Three frames were produced for three clients: the source was never asked per client.
        Assert.Equal(3, source.ProducedCount);

        // And framing happened once: every client was handed the very same buffer.
        var first = clients[0].ReceivedFrames.ToArray();
        for (var frame = 0; frame < 3; frame++)
        {
            Assert.Same(first[frame], clients[1].ReceivedFrames.ToArray()[frame]);
            Assert.Same(first[frame], clients[2].ReceivedFrames.ToArray()[frame]);
        }
    }

    [Fact]
    public async Task Start_PrefixesEveryFrameWithItsEnvelope()
    {
        var source = new FakeFrameSource(SourceStep.NothingNew, SourceStep.Changed, SourceStep.Changed);
        var session = new BroadcastSession(source, frameIntervalMs: 1, keyframeIntervalMs: NeverMs);
        var client = new RecordingVideoSink();
        session.AddClient("client-a", client);

        session.Start();
        await Eventually(() => client.ReceivedFrames.Count == 2, "two frames");
        session.Stop();

        var frames = client.ReceivedFrames.ToArray();
        Assert.True(frames[0].Length > VideoFrameEnvelope.Size);
        Assert.Equal(0u, EnvelopeOf(frames[0]).FrameId);
        Assert.Equal(1u, EnvelopeOf(frames[1]).FrameId);
        Assert.Equal<byte>([0], PayloadOf(frames[0]));
        Assert.Equal<byte>([1], PayloadOf(frames[1]));
    }

    [Fact]
    public async Task TheEnvelopeKeyframeFlag_ComesFromTheFrame_NotFromWhatWasAsked()
    {
        // Call 0 is forced (the broadcast has just started), call 1 and 2 are not. Frame 2 is a keyframe
        // the encoder chose on its own: the envelope must say so.
        var source = new FakeFrameSource(SourceStep.NothingNew, SourceStep.Changed, SourceStep.Changed, SourceStep.ChangedAsKeyframe);
        var session = new BroadcastSession(source, frameIntervalMs: 1, keyframeIntervalMs: NeverMs);
        var client = new RecordingVideoSink();
        session.AddClient("client-a", client);

        session.Start();
        await Eventually(() => client.ReceivedFrames.Count == 3, "three frames");
        session.Stop();

        var flags = client.ReceivedFrames.Select(f => EnvelopeOf(f).IsKeyframe).ToArray();
        Assert.Equal([true, false, true], flags);
    }

    [Fact]
    public async Task ADeltaFrame_DoesNotSatisfyAKeyframeRequest_AndNoClientStartsOnIt()
    {
        // The source ignores the forced keyframe on call 0 and returns a delta. A decoder cannot start from
        // that, so the client is not sent it, and the keyframe stays owed for the next call.
        var source = new FakeFrameSource(SourceStep.Changed, SourceStep.ChangedIgnoringForce);
        var session = new BroadcastSession(source, frameIntervalMs: 1, keyframeIntervalMs: NeverMs);
        var client = new RecordingVideoSink();
        session.AddClient("client-a", client);

        session.Start();
        await Eventually(() => client.ReceivedFrames.Count >= 1, "a first frame");
        session.Stop();

        var calls = source.Calls;
        Assert.True(calls[0].ForceKeyframe);
        Assert.False(calls[0].IsKeyframe);
        Assert.True(calls[1].ForceKeyframe, "a keyframe that was not delivered is still owed");
        var firstDelivered = EnvelopeOf(client.ReceivedFrames.First());
        Assert.True(firstDelivered.IsKeyframe);
        Assert.Equal(1u, firstDelivered.FrameId);
    }

    [Fact]
    public async Task RemoveClient_StopsDeliveryToThatClientOnly()
    {
        var source = new FakeFrameSource(SourceStep.Changed);
        var session = new BroadcastSession(source, frameIntervalMs: 1, keyframeIntervalMs: NeverMs);
        var staying = new RecordingVideoSink();
        var leaving = new RecordingVideoSink();
        session.AddClient("staying", staying);
        session.AddClient("leaving", leaving);

        session.Start();
        await Eventually(() => leaving.ReceivedFrames.Count >= 3, "the leaving client to receive some frames");
        session.RemoveClient("leaving");
        var leavingCountAtRemoval = leaving.ReceivedFrames.Count;
        var stayingCountAtRemoval = staying.ReceivedFrames.Count;
        await Eventually(() => staying.ReceivedFrames.Count >= stayingCountAtRemoval + 5, "the staying client to carry on");
        session.Stop();

        Assert.Equal(leavingCountAtRemoval, leaving.ReceivedFrames.Count);
        Assert.Equal(0, session.FramesOfferedTo("leaving"));
    }

    [Fact]
    public void RemoveClient_ForAnUnknownId_DoesNothing()
    {
        var session = new BroadcastSession(new FakeFrameSource(SourceStep.NothingNew), frameIntervalMs: 1);

        session.RemoveClient("nobody");
    }

    [Fact]
    public async Task AddClient_WithAnExistingId_ReplacesTheOldClient()
    {
        var source = new FakeFrameSource(SourceStep.Changed);
        var session = new BroadcastSession(source, frameIntervalMs: 1, keyframeIntervalMs: NeverMs);
        var old = new RecordingVideoSink();
        var replacement = new RecordingVideoSink();
        session.AddClient("pc", old);
        session.Start();
        await Eventually(() => old.ReceivedFrames.Count >= 2, "the first client to receive frames");

        session.AddClient("pc", replacement);
        var oldCountAfterReplace = old.ReceivedFrames.Count;
        await Eventually(() => replacement.ReceivedFrames.Count >= 3, "the replacement to receive frames");
        session.Stop();

        Assert.Equal(oldCountAfterReplace, old.ReceivedFrames.Count);
        Assert.True(EnvelopeOf(replacement.ReceivedFrames.First()).IsKeyframe);
    }

    [Fact]
    public async Task AClientAddedWhileRunning_StartsOnAKeyframe_EvenOnAChangingScreen()
    {
        var source = new FakeFrameSource(SourceStep.Changed);
        var session = new BroadcastSession(source, frameIntervalMs: 1, keyframeIntervalMs: NeverMs);
        var early = new RecordingVideoSink();
        session.AddClient("early", early);
        session.Start();
        await Eventually(() => early.ReceivedFrames.Count >= 3, "the early client to receive frames");

        var late = new RecordingVideoSink();
        session.AddClient("late", late);
        await Eventually(() => late.ReceivedFrames.Count >= 3, "the late client to receive frames");
        session.Stop();

        Assert.True(EnvelopeOf(late.ReceivedFrames.First()).IsKeyframe);
    }

    // ---- backpressure ----------------------------------------------------------------------------------

    [Fact]
    public async Task SlowClient_AbortsItsOwnFrameStreams_WithoutBlockingOtherClients()
    {
        var source = new FakeFrameSource(SourceStep.Changed);
        var session = new BroadcastSession(source, frameIntervalMs: 1, perClientQueueCapacity: 2, keyframeIntervalMs: NeverMs);
        var fast = new RecordingVideoSink();
        var slow = new StalledVideoSink();
        session.AddClient("fast", fast);
        session.AddClient("slow", slow);

        session.Start();
        await Eventually(
            () => fast.ReceivedFrames.Count > 5 && session.DroppedFrameCountFor("slow") > 0 && slow.AbortedFrameCount > 0,
            "the fast client to keep receiving while the slow one drops and aborts");
        var dropped = session.DroppedFrameCountFor("slow");
        session.Stop();

        Assert.True(dropped > 0); // the slow client dropped frames instead of stalling the session
        Assert.True(slow.AbortedFrameCount > 0); // and its in-flight frame streams were actually aborted
    }

    [Fact]
    public async Task DroppedCount_CoversEveryFrameThatNeverArrived()
    {
        // The stalled sink delivers nothing, so at any quiet moment every frame offered to it is either
        // dropped (evicted, or aborted in flight) or still waiting: at most 2 queued and 1 in flight.
        var clock = new ManualTimeProvider();
        var source = new FakeFrameSource(SourceStep.Changed);
        var session = new BroadcastSession(source, frameIntervalMs: 1, perClientQueueCapacity: 2, keyframeIntervalMs: NeverMs, timeProvider: clock);
        session.AddClient("slow", new StalledVideoSink());
        session.Start();
        await Eventually(() => source.CallCount >= 1, "the first tick");
        for (var i = 0; i < 20; i++)
        {
            await TickAsync(source, clock, 1);
        }

        await Eventually(() => session.FramesOfferedTo("slow") == 21, "21 frames offered");
        await Eventually(() => session.DroppedFrameCountFor("slow") >= 21 - 3, "all but the queued and in-flight frames dropped");
        var dropped = session.DroppedFrameCountFor("slow");
        var offered = session.FramesOfferedTo("slow");
        session.Stop();

        Assert.True(dropped <= offered);
    }

    [Fact]
    public async Task AStalledClient_LosesFrames_ButIsNotDisconnected_AndOthersAreUnaffected()
    {
        // One second of the broadcast, on a clock the test moves: 100 ticks of 10 ms.
        var clock = new ManualTimeProvider();
        var source = new FakeFrameSource(SourceStep.Changed);
        var session = new BroadcastSession(source, frameIntervalMs: 10, keyframeIntervalMs: NeverMs, timeProvider: clock);
        var fast = new RecordingVideoSink();
        var stalled = new StalledVideoSink();
        session.AddClient("fast", fast);
        session.AddClient("stalled", stalled);
        var disconnected = new List<string>();
        session.ClientDisconnected += id => { lock (disconnected) { disconnected.Add(id); } };

        session.Start();
        await Eventually(() => source.CallCount >= 1, "the first tick");
        for (var i = 0; i < 100; i++)
        {
            await TickAsync(source, clock, 10, keepUpWith: fast);
        }

        await Eventually(() => session.DroppedFrameCountFor("stalled") > 0, "the stalled client to have lost frames");
        session.Stop();

        Assert.Equal(101, fast.ReceivedFrames.Count);
        Assert.Empty(disconnected);
    }

    [Fact]
    public async Task ASendThatOutlastsItsDeadline_IsCancelled_CountedAndNotADisconnect_AfterAtLeast200ms()
    {
        // frameIntervalMs 10 => deadline max(200, 20) = 200 ms. A queue big enough that nothing is evicted,
        // so the deadline is the only thing that can drop a frame.
        var clock = new ManualTimeProvider();
        var source = new FakeFrameSource(SourceStep.Changed);
        var session = new BroadcastSession(source, frameIntervalMs: 10, perClientQueueCapacity: 100, keyframeIntervalMs: NeverMs, timeProvider: clock);
        var stalled = new StalledVideoSink();
        session.AddClient("stalled", stalled);
        var disconnected = 0;
        session.ClientDisconnected += _ => Interlocked.Increment(ref disconnected);

        session.Start();
        await Eventually(() => stalled.StartedFrameCount >= 1, "the first send to begin, at time zero");
        for (var i = 0; i < 19; i++)
        {
            await TickAsync(source, clock, 10);
        }

        // 190 ms in: the first send is still inside its deadline.
        Assert.Equal(0, session.DroppedFrameCountFor("stalled"));
        Assert.Equal(0, stalled.AbortedFrameCount);

        await TickAsync(source, clock, 10); // 200 ms
        await Eventually(() => session.DroppedFrameCountFor("stalled") == 1, "the first send to be cancelled at its deadline");
        var aborted = stalled.AbortedFrameCount;
        session.Stop();

        Assert.Equal(1, aborted);
        Assert.Equal(0, disconnected);
    }

    [Fact]
    public async Task TheSendDeadline_IsTwiceTheFrameInterval_WhenThatIsLongerThan200ms()
    {
        // frameIntervalMs 300 => deadline max(200, 600) = 600 ms.
        var clock = new ManualTimeProvider();
        var source = new FakeFrameSource(SourceStep.Changed);
        var session = new BroadcastSession(source, frameIntervalMs: 300, perClientQueueCapacity: 100, keyframeIntervalMs: NeverMs, timeProvider: clock);
        var stalled = new StalledVideoSink();
        session.AddClient("stalled", stalled);

        session.Start();
        await Eventually(() => stalled.StartedFrameCount >= 1, "the first send to begin, at time zero");
        clock.Advance(TimeSpan.FromMilliseconds(500));
        await Eventually(() => source.CallCount >= 2, "the tick at 300 ms");

        Assert.Equal(0, session.DroppedFrameCountFor("stalled")); // 500 ms: still inside 600 ms

        clock.Advance(TimeSpan.FromMilliseconds(100)); // 600 ms
        await Eventually(() => session.DroppedFrameCountFor("stalled") == 1, "the first send to be cancelled at 600 ms");
        session.Stop();
    }

    // Stands in for a QuicChannel whose per-frame stream never finishes sending. Cancelling the token is
    // exactly what QuicChannel turns into stream.Abort(QuicAbortDirection.Write, ...).
    private sealed class StalledVideoSink : IVideoSink
    {
        private int _startedFrameCount;
        private int _abortedFrameCount;

        public int StartedFrameCount => Volatile.Read(ref _startedFrameCount);

        public int AbortedFrameCount => Volatile.Read(ref _abortedFrameCount);

        public async ValueTask SendFrameAsync(byte[] envelopeAndFrame, CancellationToken ct)
        {
            Interlocked.Increment(ref _startedFrameCount);
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
                Interlocked.Increment(ref _abortedFrameCount);
                throw;
            }
        }
    }

    // ---- disconnects -----------------------------------------------------------------------------------

    [Fact]
    public async Task ClientDisconnected_FiresOnce_ForThatClientOnly_WhenItsSinkThrows()
    {
        var source = new FakeFrameSource(SourceStep.Changed);
        var session = new BroadcastSession(source, frameIntervalMs: 1, keyframeIntervalMs: NeverMs);
        var healthy = new RecordingVideoSink();
        session.AddClient("healthy", healthy);
        session.AddClient("failing", new ThrowingVideoSink());
        var disconnected = new List<string>();
        session.ClientDisconnected += id => { lock (disconnected) { disconnected.Add(id); } };

        session.Start();
        await Eventually(() => { lock (disconnected) { return disconnected.Count > 0; } }, "the failing client to be reported");
        var healthyAtDisconnect = healthy.ReceivedFrames.Count;
        await Eventually(() => healthy.ReceivedFrames.Count >= healthyAtDisconnect + 5, "the healthy client to carry on");
        var offeredToFailing = session.FramesOfferedTo("failing");
        session.Stop();

        Assert.Equal(["failing"], disconnected);
        Assert.Equal(0, offeredToFailing); // a disconnected client is no longer a client
    }

    [Fact]
    public async Task AnOperationCanceledException_TheSessionDidNotCause_IsADisconnect()
    {
        // A sink that throws OperationCanceledException on its own, not because the session cancelled it,
        // has failed; only the session's own aborts and deadlines are "this frame was shed".
        var source = new FakeFrameSource(SourceStep.Changed);
        var session = new BroadcastSession(source, frameIntervalMs: 1, keyframeIntervalMs: NeverMs);
        session.AddClient("odd", new SelfCancellingVideoSink());
        var disconnected = new List<string>();
        session.ClientDisconnected += id => { lock (disconnected) { disconnected.Add(id); } };

        session.Start();
        await Eventually(() => { lock (disconnected) { return disconnected.Count > 0; } }, "the odd client to be reported");
        session.Stop();

        Assert.Equal(["odd"], disconnected);
    }

    [Fact]
    public async Task ARejoiningClient_IsAddedBackAndStartsOnAKeyframe()
    {
        var source = new FakeFrameSource(SourceStep.Changed);
        var session = new BroadcastSession(source, frameIntervalMs: 1, keyframeIntervalMs: NeverMs);
        session.AddClient("pc", new ThrowingVideoSink());
        var disconnected = 0;
        session.ClientDisconnected += _ => Interlocked.Increment(ref disconnected);
        session.Start();
        await Eventually(() => Volatile.Read(ref disconnected) == 1, "the first connection to drop");

        var rejoined = new RecordingVideoSink();
        session.AddClient("pc", rejoined);
        await Eventually(() => rejoined.ReceivedFrames.Count >= 2, "the rejoined client to receive frames");
        session.Stop();

        Assert.True(EnvelopeOf(rejoined.ReceivedFrames.First()).IsKeyframe);
        Assert.Equal(1, disconnected);
    }

    private sealed class ThrowingVideoSink : IVideoSink
    {
        public ValueTask SendFrameAsync(byte[] envelopeAndFrame, CancellationToken ct) =>
            throw new InvalidOperationException("simulated QUIC connection drop");
    }

    private sealed class SelfCancellingVideoSink : IVideoSink
    {
        public ValueTask SendFrameAsync(byte[] envelopeAndFrame, CancellationToken ct) =>
            throw new OperationCanceledException("not ours");
    }

    // ---- a still screen --------------------------------------------------------------------------------

    [Fact]
    public async Task StillScreen_DoesNotEndTheBroadcast()
    {
        // A change, thirty ticks of nothing new, then a change. The second change has to arrive.
        var script = new[] { SourceStep.Changed }
            .Concat(Enumerable.Repeat(SourceStep.NothingNew, 30))
            .Append(SourceStep.Changed)
            .ToArray();
        var source = new FakeFrameSource(SourceStep.NothingNew, script);
        var session = new BroadcastSession(source, frameIntervalMs: 1, keyframeIntervalMs: NeverMs);
        var client = new RecordingVideoSink();
        session.AddClient("client-a", client);

        session.Start();
        await Eventually(() => client.ReceivedFrames.Count == 2, "the change after the still period to arrive");
        session.Stop();

        var frames = client.ReceivedFrames.ToArray();
        Assert.Equal<byte>([0], PayloadOf(frames[0]));
        Assert.Equal<byte>([1], PayloadOf(frames[1]));
        Assert.True(source.CallCount > 30);
    }

    [Fact]
    public async Task SourceFailure_IsSurfacedOnce_AndStopsTheSession()
    {
        var clock = new ManualTimeProvider();
        var failure = new InvalidOperationException("capture device lost");
        var source = new FakeFrameSource(SourceStep.Changed, SourceStep.Changed, SourceStep.Throw) { FailureToThrow = failure };
        var session = new BroadcastSession(source, frameIntervalMs: 100, keyframeIntervalMs: NeverMs, timeProvider: clock);
        var client = new RecordingVideoSink();
        session.AddClient("client-a", client);
        var failures = new List<Exception>();
        session.SourceFailed += ex => { lock (failures) { failures.Add(ex); } };

        session.Start();
        await Eventually(() => client.ReceivedFrames.Count == 1, "the first frame");
        await TickAsync(source, clock, 100); // the call that throws
        await Eventually(() => { lock (failures) { return failures.Count > 0; } }, "the failure to be surfaced");

        // Time keeps passing; the session does not spin on the source, call it again or report again.
        for (var i = 0; i < 5; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(100));
        }

        await Task.Delay(50);
        session.Stop();

        lock (failures)
        {
            Assert.Same(failure, Assert.Single(failures));
        }

        Assert.Equal(2, source.CallCount);
        Assert.Equal(0, session.FramesOfferedTo("client-a")); // behaves as stopped: its clients are gone
    }

    [Fact]
    public async Task ASessionThatFailedCanBeStartedAgain_FromAFreshFrameCounter()
    {
        var clock = new ManualTimeProvider();
        var source = new FakeFrameSource(SourceStep.Changed, SourceStep.Throw); // fails on its first call, then recovers
        var session = new BroadcastSession(source, frameIntervalMs: 100, keyframeIntervalMs: NeverMs, timeProvider: clock);
        var failed = 0;
        session.SourceFailed += _ => Interlocked.Increment(ref failed);
        session.Start();
        await Eventually(() => source.CallCount >= 1, "the first call");
        await Eventually(() => Volatile.Read(ref failed) == 1, "the failure");

        var again = new RecordingVideoSink();
        session.AddClient("client-a", again);
        session.Start();
        await Eventually(() => again.ReceivedFrames.Count >= 1, "a frame after the restart");
        session.Stop();

        var first = EnvelopeOf(again.ReceivedFrames.First());
        Assert.Equal(0u, first.FrameId);
        Assert.True(first.IsKeyframe);
    }

    [Fact]
    public async Task AClientAddedDuringAStillScreen_GetsAKeyframe_WithoutWaitingForAChange()
    {
        var clock = new ManualTimeProvider();
        var source = new FakeFrameSource(SourceStep.Still, SourceStep.Changed);
        var session = new BroadcastSession(source, frameIntervalMs: 100, keyframeIntervalMs: NeverMs, timeProvider: clock);
        var first = new RecordingVideoSink();
        session.AddClient("first", first);
        session.Start();
        await Eventually(() => first.ReceivedFrames.Count == 1, "the first frame");

        // The screen is now still. A second PC joins.
        var second = new RecordingVideoSink();
        session.AddClient("second", second);
        await TickAsync(source, clock, 100);
        await Eventually(() => second.ReceivedFrames.Count == 1, "the new client to receive a frame");

        // And the tick after that has nothing to send and asks for nothing.
        await TickAsync(source, clock, 100);
        session.Stop();

        Assert.True(EnvelopeOf(second.ReceivedFrames.First()).IsKeyframe);
        var calls = source.Calls;
        Assert.True(calls[1].ForceKeyframe, "the session forced a keyframe for the client that joined");
        Assert.True(calls[1].Produced);
        Assert.False(calls[2].ForceKeyframe, "and only once");
        Assert.Single(second.ReceivedFrames);
    }

    [Fact]
    public async Task StillScreen_SendsAKeyframe_EveryKeyframeInterval()
    {
        // 100 ms ticks, a keyframe every 1000 ms, a picture that never changes after the first frame.
        var clock = new ManualTimeProvider();
        var source = new FakeFrameSource(SourceStep.Still, SourceStep.Changed);
        var session = new BroadcastSession(source, frameIntervalMs: 100, keyframeIntervalMs: 1000, timeProvider: clock);
        var client = new RecordingVideoSink();
        session.AddClient("client-a", client);

        session.Start();
        await Eventually(() => source.CallCount >= 1, "the first tick");
        for (var tick = 1; tick <= 30; tick++)
        {
            await TickAsync(source, clock, 100);
        }

        await Eventually(() => client.ReceivedFrames.Count == 4, "four keyframes: at 0, 1000, 2000 and 3000 ms");
        session.Stop();

        var calls = source.Calls;
        Assert.Equal([0, 10, 20, 30], calls.Where(c => c.ForceKeyframe).Select(c => c.Index).ToArray());
        Assert.Equal(4, client.ReceivedFrames.Count);
        Assert.All(client.ReceivedFrames, f => Assert.True(EnvelopeOf(f).IsKeyframe));
    }

    [Fact]
    public async Task AChangingScreen_ForcesAKeyframe_OnlyOnTheInterval()
    {
        var clock = new ManualTimeProvider();
        var source = new FakeFrameSource(SourceStep.Changed);
        var session = new BroadcastSession(source, frameIntervalMs: 100, keyframeIntervalMs: 1000, timeProvider: clock);
        var client = new RecordingVideoSink();
        session.AddClient("client-a", client);

        session.Start();
        await Eventually(() => source.CallCount >= 1, "the first tick");
        for (var tick = 1; tick <= 25; tick++)
        {
            await TickAsync(source, clock, 100, keepUpWith: client);
        }

        await Eventually(() => client.ReceivedFrames.Count == 26, "every frame of a changing screen");
        session.Stop();

        Assert.Equal([0, 10, 20], source.Calls.Where(c => c.ForceKeyframe).Select(c => c.Index).ToArray());
        var keyframeIds = client.ReceivedFrames.Select(EnvelopeOf).Where(e => e.IsKeyframe).Select(e => e.FrameId).ToArray();
        Assert.Equal([0u, 10u, 20u], keyframeIds);
    }

    // ---- stopping --------------------------------------------------------------------------------------

    [Fact]
    public async Task Stop_EndsTheLoop_Promptly()
    {
        // A source that blocks for its whole timeout whenever nothing changed, as a real capture does.
        var source = new FakeFrameSource(SourceStep.NothingNew) { BlockForTimeoutWhenNothingNew = true };
        var session = new BroadcastSession(source, frameIntervalMs: 50, keyframeIntervalMs: NeverMs);
        session.AddClient("client-a", new RecordingVideoSink());
        session.Start();
        await Eventually(() => source.CallCount >= 2, "the loop to be running");

        var clock = Stopwatch.StartNew();
        session.Stop();
        var stopTook = clock.Elapsed;
        var callsAtStop = source.CallCount;
        await Task.Delay(300); // several intervals: a loop that survived Stop would have called the source again

        Assert.True(stopTook < TimeSpan.FromSeconds(5), $"Stop took {stopTook.TotalMilliseconds:0} ms");
        Assert.Equal(callsAtStop, source.CallCount);
    }

    [Fact]
    public async Task Stop_LeavesNoTimerOrTaskBehind_SoLaterTimeDoesNothing()
    {
        var clock = new ManualTimeProvider();
        var source = new FakeFrameSource(SourceStep.Changed);
        var session = new BroadcastSession(source, frameIntervalMs: 10, keyframeIntervalMs: NeverMs, timeProvider: clock);
        session.AddClient("client-a", new RecordingVideoSink());
        session.Start();
        await Eventually(() => source.CallCount >= 1, "the first tick");

        session.Stop();
        var callsAtStop = source.CallCount;
        for (var i = 0; i < 10; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(10));
        }

        await Task.Delay(50);

        Assert.Equal(callsAtStop, source.CallCount);
    }

    [Fact]
    public void Stop_WithoutStart_AndTwice_IsHarmless()
    {
        var session = new BroadcastSession(new FakeFrameSource(SourceStep.NothingNew), frameIntervalMs: 10);

        session.Stop();
        session.Stop();
    }

    [Fact]
    public async Task Start_WhileRunning_Throws_AndAfterStop_StartsAgainFromFrameZero()
    {
        var source = new FakeFrameSource(SourceStep.Changed);
        var session = new BroadcastSession(source, frameIntervalMs: 1, keyframeIntervalMs: NeverMs);
        var before = new RecordingVideoSink();
        session.AddClient("a", before);
        session.Start();
        await Eventually(() => before.ReceivedFrames.Count >= 2, "frames");

        Assert.Throws<InvalidOperationException>(session.Start);
        session.Stop();

        var after = new RecordingVideoSink();
        session.AddClient("a", after);
        session.Start();
        await Eventually(() => after.ReceivedFrames.Count >= 1, "frames after the restart");
        session.Stop();

        var first = EnvelopeOf(after.ReceivedFrames.First());
        Assert.Equal(0u, first.FrameId);
        Assert.True(first.IsKeyframe);
    }

    [Fact]
    public async Task EventHandlers_MayCallStop_WithoutDeadlock()
    {
        var source = new FakeFrameSource(SourceStep.Changed);
        var session = new BroadcastSession(source, frameIntervalMs: 1, keyframeIntervalMs: NeverMs);
        session.AddClient("healthy", new RecordingVideoSink());
        session.AddClient("failing", new ThrowingVideoSink());
        var handled = 0;
        session.ClientDisconnected += _ =>
        {
            session.Stop();
            Interlocked.Increment(ref handled);
        };

        session.Start();
        await Eventually(() => Volatile.Read(ref handled) == 1, "a handler that calls Stop to return");

        var failingSource = new FakeFrameSource(SourceStep.Throw);
        var second = new BroadcastSession(failingSource, frameIntervalMs: 1);
        var secondHandled = 0;
        second.SourceFailed += _ =>
        {
            second.Stop();
            Interlocked.Increment(ref secondHandled);
        };
        second.Start();
        await Eventually(() => Volatile.Read(ref secondHandled) == 1, "a SourceFailed handler that calls Stop to return");
    }

    [Fact]
    public void TheConstructor_RejectsValuesThatCannotWork()
    {
        var source = new FakeFrameSource(SourceStep.NothingNew);

        Assert.Throws<ArgumentOutOfRangeException>(() => new BroadcastSession(source, frameIntervalMs: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BroadcastSession(source, frameIntervalMs: 10, perClientQueueCapacity: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BroadcastSession(source, frameIntervalMs: 10, keyframeIntervalMs: 0));
        Assert.Throws<ArgumentNullException>(() => new BroadcastSession(null!, frameIntervalMs: 10));
    }

    // ---- FramesOfferedTo (Amendment 1) -----------------------------------------------------------------

    [Fact]
    public async Task FramesOfferedTo_CountsEveryFrameQueuedForThatClient()
    {
        var source = new FakeFrameSource(SourceStep.NothingNew, [.. Enumerable.Repeat(SourceStep.Changed, 5)]);
        var session = new BroadcastSession(source, frameIntervalMs: 1, keyframeIntervalMs: NeverMs);
        var sink = new RecordingVideoSink();
        session.AddClient("a", sink);

        session.Start();
        await Eventually(() => sink.ReceivedFrames.Count == 5, "all five frames");
        var offered = session.FramesOfferedTo("a");
        session.Stop();

        Assert.Equal(5, offered);
    }

    [Fact]
    public async Task FramesOfferedTo_IncludesFramesThatWereDropped()
    {
        var source = new FakeFrameSource(SourceStep.Changed);
        var session = new BroadcastSession(source, frameIntervalMs: 1, perClientQueueCapacity: 2, keyframeIntervalMs: NeverMs);
        session.AddClient("slow", new StalledVideoSink());

        session.Start();
        await Eventually(() => session.DroppedFrameCountFor("slow") > 0, "the slow client to drop a frame");
        var dropped = session.DroppedFrameCountFor("slow"); // read the drops first: offered only ever grows
        var offered = session.FramesOfferedTo("slow");
        session.Stop();

        Assert.True(dropped > 0);
        Assert.True(offered >= dropped);
    }

    [Fact]
    public void FramesOfferedTo_IsZero_ForAnUnknownClient()
    {
        var session = new BroadcastSession(new FakeFrameSource(SourceStep.NothingNew), frameIntervalMs: 1);

        Assert.Equal(0, session.FramesOfferedTo("nobody"));
        Assert.Equal(0, session.DroppedFrameCountFor("nobody"));
    }
}
