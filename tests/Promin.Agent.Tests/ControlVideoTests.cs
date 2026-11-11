using System.Collections.Concurrent;
using System.Diagnostics;
using Promin.Protocol.Broadcast;
using Promin.Protocol.Transport;

namespace Promin.Agent.Tests;

public class ControlVideoTests
{
    private sealed class FakeSource : IEncodedFrameSource, IDisposable
    {
        private int _calls;

        public ConcurrentQueue<EncodedVideoFrame> Frames { get; } = new();
        public ConcurrentQueue<bool> Forced { get; } = new();
        public Exception? Throws { get; set; }
        public bool Disposed { get; private set; }
        public int Calls => Volatile.Read(ref _calls);

        /// <summary>Runs in each call, after <see cref="Calls"/> counts it and before a frame is taken.</summary>
        public Action? OnCall { get; set; }

        public bool TryGetNextFrame(TimeSpan timeout, bool forceKeyframe, out EncodedVideoFrame frame)
        {
            Forced.Enqueue(forceKeyframe);
            Interlocked.Increment(ref _calls);
            OnCall?.Invoke();
            if (Throws is { } failure)
            {
                throw failure;
            }

            return Frames.TryDequeue(out frame);
        }

        public void Dispose() => Disposed = true;
    }

    private readonly ManualTimeProvider _clock = new();
    private readonly FakeSource _source = new();
    private readonly ConcurrentQueue<byte[]> _sent = new();

    private uint _nextId;

    private ControlVideo Video() => new(_source, 30, (bytes, _) =>
    {
        _sent.Enqueue(bytes);
        return ValueTask.CompletedTask;
    }, () => _nextId++, _clock);

    private static EncodedVideoFrame Frame(byte b, bool key = false) => new([b, b], key);

    private static async Task Eventually(Func<bool> condition, string what)
    {
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            if (clock.Elapsed > TimeSpan.FromSeconds(5))
            {
                Assert.Fail($"Timed out waiting for: {what}");
            }

            await Task.Delay(1);
        }
    }

    // Waits until tick number <paramref name="n"/> has been taken and its wait started, then moves the clock past it.
    private async Task PastTick(int n)
    {
        await Eventually(() => _source.Calls == n && _clock.PendingTimers == 1, $"the wait after tick {n}");
        _clock.Advance(TimeSpan.FromMilliseconds(34));
    }

    // Runs the video for <paramref name="ticks"/> ticks that each take 40 ms of the clock, so the loop goes round without
    // waiting, as it does when the screen is still; <paramref name="onTick"/> gets each tick's number before a frame is taken.
    private async Task RunTicksAsync(ControlVideo video, int ticks, Action<int> onTick)
    {
        using var stop = new CancellationTokenSource();
        _source.OnCall = () =>
        {
            _clock.Advance(TimeSpan.FromMilliseconds(40));
            onTick(_source.Calls);
            if (_source.Calls == ticks)
            {
                stop.Cancel();
            }
        };
        await video.RunAsync(stop.Token);
    }

    [Fact]
    public async Task TheFirstFrame_IsAKeyframeRequest()
    {
        using var stop = new CancellationTokenSource();
        var run = Video().RunAsync(stop.Token);

        await Eventually(() => _source.Calls == 1, "the first tick");
        await stop.CancelAsync();
        await run;

        Assert.True(_source.Forced.First());
    }

    [Fact]
    public async Task EachFrame_IsTaggedAsControlVideo_WithAnEnvelope()
    {
        _source.Frames.Enqueue(Frame(7, key: true));
        using var stop = new CancellationTokenSource();
        var run = Video().RunAsync(stop.Token);

        await Eventually(() => _sent.Count == 1, "the frame");
        await stop.CancelAsync();
        await run;

        Assert.Equal(new byte[] { AgentStream.ControlVideo, 0, 0, 0, 0, 1, 7, 7 }, _sent.Single());
    }

    [Fact]
    public async Task FrameIds_ComeFromTheCounter_OnePerFrameSent()
    {
        _nextId = 40;
        _source.Frames.Enqueue(Frame(1, key: true));
        using var stop = new CancellationTokenSource();
        var run = Video().RunAsync(stop.Token);

        await PastTick(1);
        await PastTick(2); // nothing to send in tick 2
        _source.Frames.Enqueue(Frame(2));
        _source.Frames.Enqueue(Frame(3));
        await PastTick(3);
        await PastTick(4);
        await Eventually(() => _sent.Count == 3, "three frames");
        await stop.CancelAsync();
        await run;

        Assert.Equal([40, 41, 42], _sent.Select(b => (int)b[4]));
    }

    [Fact]
    public async Task RequestKeyframe_ForcesTheNextFrame_Once()
    {
        var video = Video();
        _source.Frames.Enqueue(Frame(1, key: true));

        await RunTicksAsync(video, 4, tick =>
        {
            if (tick == 2)
            {
                video.RequestKeyframe();
            }
            else if (tick == 3)
            {
                _source.Frames.Enqueue(Frame(2, key: true));
            }
        });

        Assert.Equal([true, false, true, false], _source.Forced);
    }

    [Fact]
    public async Task AForcedTickThatGivesNoKeyframe_ForcesTheNextOneToo()
    {
        var video = Video();
        _source.Frames.Enqueue(Frame(1, key: true));

        await RunTicksAsync(video, 6, tick =>
        {
            switch (tick)
            {
                case 2: video.RequestKeyframe(); break; // tick 3 gives nothing
                case 4: _source.Frames.Enqueue(Frame(2)); break;
                case 5: _source.Frames.Enqueue(Frame(3, key: true)); break;
            }
        });

        Assert.Equal([true, false, true, true, true, false], _source.Forced);
    }

    [Fact]
    public async Task AKeyframe_IsForcedTwoSecondsAfterTheLastOne()
    {
        var video = Video();
        _source.Frames.Enqueue(Frame(1, key: true));

        // Ticks start 40 ms apart: tick 50 at 1960 ms, tick 51 at 2000 ms.
        await RunTicksAsync(video, 52, tick =>
        {
            if (tick == 51)
            {
                _source.Frames.Enqueue(Frame(2, key: true));
            }
        });

        Assert.Equal([true, .. Enumerable.Repeat(false, 49), true, false], _source.Forced);
    }

    [Fact]
    public async Task ItAsksForAFrameOncePerTick()
    {
        using var stop = new CancellationTokenSource();
        var run = Video().RunAsync(stop.Token);

        await Eventually(() => _source.Calls == 1 && _clock.PendingTimers == 1, "the first tick");
        _clock.Advance(TimeSpan.FromMilliseconds(32));
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.Equal(1, _source.Calls);

        _clock.Advance(TimeSpan.FromMilliseconds(2));
        await Eventually(() => _source.Calls == 2, "the second tick 33 ms after the first");

        await stop.CancelAsync();
        await run;
    }

    [Fact]
    public async Task ASourceThatThrows_EndsRunAsync_WithThatException()
    {
        _source.Throws = new InvalidOperationException("encoder gone");

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => Video().RunAsync(CancellationToken.None));

        Assert.Equal("encoder gone", failure.Message);
    }

    [Fact]
    public void Dispose_DisposesTheSource()
    {
        Video().Dispose();

        Assert.True(_source.Disposed);
    }
}
