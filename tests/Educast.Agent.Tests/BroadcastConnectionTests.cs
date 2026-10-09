using System.Threading.Channels;
using Educast.Protocol.Messages;

namespace Educast.Agent.Tests;

/// <summary>
/// What the agent does with one teacher PC's connection: a display exists exactly while a broadcast does, and
/// its life is tied to the connection and to StopBroadcast, never to how long it has been since a frame came.
/// </summary>
public class BroadcastConnectionTests
{
    private sealed class FakeConnection : IAgentConnection
    {
        private readonly Channel<ControlMessage> _control = Channel.CreateUnbounded<ControlMessage>();
        private readonly Channel<byte[]> _video = Channel.CreateUnbounded<byte[]>();

        public Task<ControlMessage> ReceiveControlAsync(CancellationToken ct) => _control.Reader.ReadAsync(ct).AsTask();

        public ValueTask<byte[]> ReceiveVideoFrameAsync(CancellationToken ct) => _video.Reader.ReadAsync(ct);

        public List<ControlMessage> Sent { get; } = [];

        public Task SendControlAsync(ControlMessage message, CancellationToken ct)
        {
            lock (Sent)
            {
                Sent.Add(message);
            }

            return Task.CompletedTask;
        }

        public int SentReady
        {
            get
            {
                lock (Sent)
                {
                    return Sent.Count(m => m is BroadcastReady);
                }
            }
        }

        public void Send(ControlMessage message) => _control.Writer.TryWrite(message);

        public void SendFrame(params byte[] payload) => _video.Writer.TryWrite(payload);

        /// <summary>The connection dies: both streams fail, as they do when QUIC's idle timeout ends it.</summary>
        public void Lose(Exception? how = null)
        {
            _control.Writer.TryComplete(how ?? new IOException("the connection was lost"));
            _video.Writer.TryComplete(how ?? new IOException("the connection was lost"));
        }

        public int UnreadFrames => _video.Reader.Count;
    }

    private sealed class FakeDisplay : IBroadcastDisplay
    {
        private int _disposed;
        public List<byte[]> Frames { get; } = [];
        public int Restarts;
        public bool IsOpen { get; set; } = true;
        public bool Disposed => Volatile.Read(ref _disposed) != 0;

        public ValueTask SubmitFrameAsync(byte[] streamPayload, CancellationToken ct)
        {
            lock (Frames)
            {
                Frames.Add(streamPayload);
            }

            return ValueTask.CompletedTask;
        }

        public ValueTask RestartAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref Restarts);
            return ValueTask.CompletedTask;
        }

        public void Dispose() => Interlocked.Exchange(ref _disposed, 1);

        public byte[][] Received
        {
            get
            {
                lock (Frames)
                {
                    return [.. Frames];
                }
            }
        }
    }

    private sealed class FakeFactory : IBroadcastDisplayFactory
    {
        private readonly Queue<Func<FakeDisplay?>> _script = new();
        public List<FakeDisplay> Created { get; } = [];
        public List<StartBroadcast> Asked { get; } = [];

        /// <summary>The next call produces a display; with no script, every call does.</summary>
        public FakeFactory Then(Func<FakeDisplay?> next)
        {
            _script.Enqueue(next);
            return this;
        }

        public Task<IBroadcastDisplay?> StartAsync(StartBroadcast message)
        {
            Asked.Add(message);
            var display = _script.TryDequeue(out var next) ? next() : new FakeDisplay();
            if (display is not null)
            {
                Created.Add(display);
            }

            return Task.FromResult<IBroadcastDisplay?>(display);
        }
    }

    private sealed class Rig : IAsyncDisposable
    {
        private readonly CancellationTokenSource _stop = new(TimeSpan.FromSeconds(20));
        private Task? _running;

        public Rig(FakeFactory? factory = null)
        {
            Factory = factory ?? new FakeFactory();
            Handler = new BroadcastConnection(Factory, Log.Add);
        }

        public FakeConnection Connection { get; } = new();
        public FakeFactory Factory { get; }
        public BroadcastConnection Handler { get; }
        public List<string> Log { get; } = [];

        public Task Running => _running!;

        public void Start() => _running = Handler.RunAsync(Connection, _stop.Token);

        public void Cancel() => _stop.Cancel();

        public async Task UntilAsync(Func<bool> condition, string what)
        {
            for (var i = 0; i < 2000 && !condition(); i++)
            {
                await Task.Delay(5);
            }

            Assert.True(condition(), $"timed out waiting for: {what}");
        }

        /// <summary>Sends StartBroadcast and waits until it has been handled: the display is in place, or it was not made.</summary>
        public async Task StartedAsync(StartBroadcast? message = null, bool expectDisplay = true)
        {
            var asked = Factory.Asked.Count;
            var created = Factory.Created.Count;
            Connection.Send(message ?? new StartBroadcast(1920, 1080, 15));
            if (expectDisplay)
            {
                await UntilAsync(
                    () => Factory.Created.Count > created && ReferenceEquals(Handler.Display, Factory.Created[^1]),
                    "the display in place");
            }
            else
            {
                await UntilAsync(
                    () => Factory.Asked.Count > asked && Handler.Display is null
                        && Log.Any(line => line.Contains("not shown") || line.Contains("could not be shown")),
                    "the broadcast reported as not shown");
            }
        }

        /// <summary>Waits until every frame the connection has offered has been read.</summary>
        public Task FramesReadAsync() => UntilAsync(() => Connection.UnreadFrames == 0, "frames read");

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            if (_running is not null)
            {
                await _running.WaitAsync(TimeSpan.FromSeconds(5));
            }

            _stop.Dispose();
        }
    }

    [Fact]
    public async Task StartBroadcast_CreatesADisplay_AndFramesReachIt_InOrder()
    {
        await using var rig = new Rig();
        rig.Start();

        await rig.StartedAsync(new StartBroadcast(1280, 720, 10));
        rig.Connection.SendFrame(1);
        rig.Connection.SendFrame(2);
        rig.Connection.SendFrame(3);
        var display = Assert.Single(rig.Factory.Created);
        await rig.UntilAsync(() => display.Received.Length == 3, "three frames shown");

        Assert.Equal(new StartBroadcast(1280, 720, 10), rig.Factory.Asked[0]);
        Assert.Equal([new byte[] { 1 }, [2], [3]], display.Received);
    }

    /// <summary>
    /// The teacher PC holds its first keyframe until the display can take it, so a display that is ready says so;
    /// one that could not be made says nothing, and a restarted one says so again.
    /// </summary>
    [Fact]
    public async Task AReadyDisplay_IsAnnouncedToTheTeacherPc_AFailedOneIsNot()
    {
        var factory = new FakeFactory().Then(() => null);
        await using var rig = new Rig(factory);
        rig.Start();

        await rig.StartedAsync(expectDisplay: false);
        Assert.Equal(0, rig.Connection.SentReady);

        await rig.StartedAsync();
        await rig.UntilAsync(() => rig.Connection.SentReady == 1, "ready announced");

        rig.Connection.Send(new StartBroadcast(1280, 720, 15));
        await rig.UntilAsync(() => rig.Connection.SentReady == 2, "ready announced again after the restart");
        Assert.Equal(1, factory.Created[0].Restarts);
    }

    [Fact]
    public async Task FramesThatArriveWithNoBroadcast_GoNowhere()
    {
        await using var rig = new Rig();
        rig.Start();

        rig.Connection.SendFrame(9);
        await rig.FramesReadAsync();
        await rig.StartedAsync();
        rig.Connection.SendFrame(10);
        var display = Assert.Single(rig.Factory.Created);
        await rig.UntilAsync(() => display.Received.Length == 1, "a frame shown");

        Assert.Equal([new byte[] { 10 }], display.Received);
    }

    [Fact]
    public async Task StopBroadcast_RemovesTheDisplay_AndLaterFramesGoNowhere()
    {
        await using var rig = new Rig();
        rig.Start();
        await rig.StartedAsync();
        var display = Assert.Single(rig.Factory.Created);

        rig.Connection.Send(new StopBroadcast());
        await rig.UntilAsync(() => display.Disposed, "display removed");
        rig.Connection.SendFrame(5);
        await rig.FramesReadAsync();

        Assert.Empty(display.Received);
        Assert.False(rig.Running.IsCompleted); // the connection itself carries on
    }

    [Fact]
    public async Task ABroadcastThatStartsAfterAStop_GetsADisplayOfItsOwn()
    {
        await using var rig = new Rig();
        rig.Start();
        await rig.StartedAsync();
        rig.Connection.Send(new StopBroadcast());
        await rig.UntilAsync(() => rig.Factory.Created[0].Disposed, "first display removed");

        await rig.StartedAsync();

        Assert.Equal(2, rig.Factory.Created.Count);
        Assert.False(rig.Factory.Created[1].Disposed);
    }

    [Fact]
    public async Task WhenTheConnectionIsLost_TheDisplayIsRemoved_AndTheHandlerEnds()
    {
        await using var rig = new Rig();
        rig.Start();
        await rig.StartedAsync();
        var display = Assert.Single(rig.Factory.Created);

        rig.Connection.Lose();
        await rig.Running.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(display.Disposed);
        Assert.Contains(rig.Log, line => line.Contains("the connection was lost"));
    }

    [Fact]
    public async Task WhenTheVideoStreamEnds_TheDisplayIsRemoved_EvenIfTheControlStreamIsStillOpen()
    {
        await using var rig = new Rig();
        var connection = new EndsVideoFirst();
        var handler = new BroadcastConnection(rig.Factory, rig.Log.Add);
        var running = handler.RunAsync(connection, CancellationToken.None);
        connection.Control.Writer.TryWrite(new StartBroadcast(1920, 1080, 15));
        await rig.UntilAsync(() => rig.Factory.Created.Count == 1, "display created");

        connection.Video.Writer.TryComplete();
        await running.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(rig.Factory.Created[0].Disposed);
    }

    private sealed class EndsVideoFirst : IAgentConnection
    {
        public Channel<ControlMessage> Control { get; } = Channel.CreateUnbounded<ControlMessage>();
        public Channel<byte[]> Video { get; } = Channel.CreateUnbounded<byte[]>();

        public Task<ControlMessage> ReceiveControlAsync(CancellationToken ct) => Control.Reader.ReadAsync(ct).AsTask();

        public ValueTask<byte[]> ReceiveVideoFrameAsync(CancellationToken ct) => Video.Reader.ReadAsync(ct);

        public Task SendControlAsync(ControlMessage message, CancellationToken ct) => Task.CompletedTask;
    }

    [Fact]
    public async Task WhenTheHandlerIsCancelled_TheDisplayIsRemoved_AndItEndsNormally()
    {
        await using var rig = new Rig();
        rig.Start();
        await rig.StartedAsync();

        rig.Cancel(); // a newer connection took over
        await rig.Running.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(rig.Factory.Created[0].Disposed);
        Assert.True(rig.Running.IsCompletedSuccessfully);
    }

    /// <summary>
    /// A still teacher screen sends no frames for as long as it stays still. That is not a reason to take the
    /// picture away: only StopBroadcast and the connection ending are.
    /// </summary>
    [Fact]
    public async Task SilenceDoesNotRemoveTheDisplay()
    {
        await using var rig = new Rig();
        rig.Start();
        await rig.StartedAsync();
        rig.Connection.SendFrame(1);
        var display = Assert.Single(rig.Factory.Created);
        await rig.UntilAsync(() => display.Received.Length == 1, "a frame shown");

        await Task.Delay(300);

        Assert.False(display.Disposed);
        Assert.False(rig.Running.IsCompleted);
    }

    [Fact]
    public async Task ASecondStartBroadcast_RestartsTheDisplayThereIs_InsteadOfMakingAnother()
    {
        await using var rig = new Rig();
        rig.Start();
        await rig.StartedAsync();
        var display = Assert.Single(rig.Factory.Created);

        rig.Connection.Send(new StartBroadcast(1280, 720, 15));
        await rig.UntilAsync(() => display.Restarts == 1, "display restarted");

        Assert.Single(rig.Factory.Created);
        Assert.Single(rig.Factory.Asked);
        Assert.False(display.Disposed);
    }

    [Fact]
    public async Task WhenTheDisplayCannotBeMade_FramesAreDropped_AndALaterStartBroadcastTriesAgain()
    {
        var factory = new FakeFactory().Then(() => null);
        await using var rig = new Rig(factory);
        rig.Start();

        await rig.StartedAsync(expectDisplay: false);
        rig.Connection.SendFrame(1);
        await rig.FramesReadAsync();
        Assert.Empty(factory.Created);
        Assert.False(rig.Running.IsCompleted); // a broadcast that cannot be shown does not end the connection

        await rig.StartedAsync();

        Assert.Equal(2, factory.Asked.Count);
        Assert.Single(factory.Created);
    }

    [Fact]
    public async Task WhenTheFactoryThrows_ItIsLogged_AndTheConnectionCarriesOn()
    {
        var factory = new FakeFactory().Then(() => throw new InvalidOperationException("no GPU today"));
        await using var rig = new Rig(factory);
        rig.Start();

        await rig.StartedAsync(expectDisplay: false);
        Assert.Contains(rig.Log, line => line.Contains("no GPU today"));

        Assert.False(rig.Running.IsCompleted);
        await rig.StartedAsync();
        Assert.Single(factory.Created);
    }

    /// <summary>
    /// The teacher PC can only tell a PC apart from a receiving one by its connection: a display that ended on its
    /// own ends the connection, with one log line, so the wall reads Reconnecting. A display removed by
    /// StopBroadcast is not "by itself" and the connection carries on (StopBroadcast_RemovesTheDisplay_...).
    /// </summary>
    [Fact]
    public async Task ADisplayThatClosedByItself_EndsTheConnection_WithOneLogLine()
    {
        await using var rig = new Rig();
        rig.Start();
        await rig.StartedAsync();
        var first = Assert.Single(rig.Factory.Created);

        first.IsOpen = false; // the student closed the window (--windowed), or the GPU failed
        rig.Connection.SendFrame(1);
        await rig.Running.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Empty(first.Received);
        Assert.True(first.Disposed);
        Assert.True(rig.Running.IsCompletedSuccessfully);
        Assert.Single(rig.Log, line => line.Contains("ended by itself"));
        Assert.DoesNotContain(rig.Log, line => line.Contains("were dropped"));
    }

    [Theory]
    [InlineData(0, 1080, 15)]
    [InlineData(1920, 0, 15)]
    [InlineData(1920, 1080, 0)]
    [InlineData(-1, -1, -1)]
    public async Task AStartBroadcastThatCannotBeRight_IsIgnored(int width, int height, int fps)
    {
        await using var rig = new Rig();
        rig.Start();

        rig.Connection.Send(new StartBroadcast(width, height, fps));
        await rig.UntilAsync(() => rig.Log.Any(line => line.Contains("Ignored")), "start ignored");

        Assert.Empty(rig.Factory.Asked);
    }

    [Fact]
    public async Task AMessageForAnotherPurpose_IsIgnored()
    {
        await using var rig = new Rig();
        rig.Start();

        rig.Connection.Send(new BroadcastReady());
        await rig.StartedAsync();

        Assert.Single(rig.Factory.Created);
        Assert.False(rig.Running.IsCompleted);
    }

    [Fact]
    public async Task ACorruptControlStream_EndsThisConnectionOnly_WithoutThrowing()
    {
        await using var rig = new Rig();
        rig.Start();
        await rig.StartedAsync();

        rig.Connection.Lose(new ControlMessageFormatException("not a control frame"));
        await rig.Running.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(rig.Running.IsCompletedSuccessfully);
        Assert.True(rig.Factory.Created[0].Disposed);
    }

    private sealed class HangingFactory : IBroadcastDisplayFactory
    {
        public TaskCompletionSource<IBroadcastDisplay?> Pending { get; } = new();
        public int Asked;

        public Task<IBroadcastDisplay?> StartAsync(StartBroadcast message)
        {
            Interlocked.Increment(ref Asked);
            return Pending.Task;
        }
    }

    [Fact(Timeout = 20000)]
    public async Task ADisplayThatNeverGetsReady_IsGivenUpOn_AndLoggedWithTheTimeLimit_WithoutBlockingTheControlLoop()
    {
        var factory = new HangingFactory();
        var log = new List<string>();
        var connection = new FakeConnection();
        var handler = new BroadcastConnection(factory, log.Add, startTimeout: TimeSpan.FromMilliseconds(100));
        var running = handler.RunAsync(connection, CancellationToken.None);

        connection.Send(new StartBroadcast(1920, 1080, 15));
        for (var i = 0; i < 500 && !log.Any(line => line.Contains("not ready after")); i++)
        {
            await Task.Delay(10);
        }

        Assert.Contains(log, line => line.Contains("not ready after 0.1 seconds"));
        Assert.Null(handler.Display);
        Assert.False(running.IsCompleted); // the connection carries on: StopBroadcast and the next start are still heard

        connection.Send(new StopBroadcast());
        connection.Lose();
        await running.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact(Timeout = 20000)]
    public async Task ADisplayThatTurnsUpAfterTheBroadcastWasGivenUpOn_IsRemoved()
    {
        var factory = new HangingFactory();
        var log = new List<string>();
        var connection = new FakeConnection();
        var handler = new BroadcastConnection(factory, log.Add, startTimeout: TimeSpan.FromMilliseconds(50));
        var running = handler.RunAsync(connection, CancellationToken.None);
        connection.Send(new StartBroadcast(1920, 1080, 15));
        for (var i = 0; i < 500 && !log.Any(line => line.Contains("not ready after")); i++)
        {
            await Task.Delay(10);
        }

        var late = new FakeDisplay();
        factory.Pending.SetResult(late);
        for (var i = 0; i < 500 && !late.Disposed; i++)
        {
            await Task.Delay(10);
        }

        Assert.True(late.Disposed);
        Assert.Null(handler.Display);
        connection.Lose();
        await running.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact(Timeout = 20000)]
    public async Task WhenTheConnectionEndsWhileADisplayIsStarting_TheHandlerEndsAtOnce_AndTheLateDisplayIsRemoved()
    {
        var factory = new HangingFactory();
        var connection = new FakeConnection();
        using var cts = new CancellationTokenSource();
        var handler = new BroadcastConnection(factory, _ => { });
        var running = handler.RunAsync(connection, cts.Token);
        connection.Send(new StartBroadcast(1920, 1080, 15));
        for (var i = 0; i < 500 && Volatile.Read(ref factory.Asked) == 0; i++)
        {
            await Task.Delay(10);
        }

        await cts.CancelAsync(); // a newer connection took over
        await running.WaitAsync(TimeSpan.FromSeconds(5));

        var late = new FakeDisplay();
        factory.Pending.SetResult(late);
        for (var i = 0; i < 500 && !late.Disposed; i++)
        {
            await Task.Delay(10);
        }

        Assert.True(late.Disposed);
    }
}
