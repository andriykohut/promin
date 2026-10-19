using System.Collections.Concurrent;
using System.Diagnostics;
using Promin.MediaPipeline.Pictures;

namespace Promin.Agent.Tests;

public class PictureSenderTests
{
    private sealed class FakePictures : IScreenPictures
    {
        private int _takes;

        public ConcurrentQueue<(PictureOutcome Outcome, byte[] Jpeg)> Next { get; } = new();
        public Exception? Throws { get; set; }
        public Exception? DisposeThrows { get; set; }
        public bool Disposed { get; private set; }
        public int Takes => Volatile.Read(ref _takes);

        public PictureOutcome TryTake(TimeSpan timeout, out byte[] jpeg)
        {
            Interlocked.Increment(ref _takes);
            if (Throws is { } failure)
            {
                throw failure;
            }

            (var outcome, jpeg) = Next.TryDequeue(out var next) ? next : (PictureOutcome.Unchanged, Array.Empty<byte>());
            return outcome;
        }

        public void Dispose()
        {
            Disposed = true;
            if (DisposeThrows is { } failure)
            {
                throw failure;
            }
        }
    }

    private readonly ConcurrentQueue<byte[]> _sent = new();
    private readonly List<string> _log = [];
    private readonly FakePictures _pictures = new();

    private static byte[] Jpeg(byte b) => [0xFF, 0xD8, b];

    private PictureSender Sender(
        Func<IScreenPictures>? open = null, Func<byte[], CancellationToken, ValueTask>? send = null, TimeProvider? time = null) =>
        new(open ?? (() => _pictures),
            send ?? ((jpeg, _) =>
            {
                _sent.Enqueue(jpeg);
                return ValueTask.CompletedTask;
            }),
            _log.Add,
            time ?? TimeProvider.System);

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

    [Fact]
    public async Task APicture_IsSent()
    {
        _pictures.Next.Enqueue((PictureOutcome.Picture, Jpeg(1)));

        await Sender().StepAsync(CancellationToken.None);

        Assert.Equal([Jpeg(1)], _sent);
    }

    [Fact]
    public async Task AnUnchangedScreen_SendsNothing()
    {
        await Sender().StepAsync(CancellationToken.None);

        Assert.Empty(_sent);
    }

    // A PC locked when the console connects says so first, instead of leaving the tile empty.
    [Fact]
    public async Task ScreenNotAvailable_IsSentOnce_AsAnEmptyPicture_UntilAPictureClearsIt()
    {
        var sender = Sender();
        _pictures.Next.Enqueue((PictureOutcome.Unavailable, []));
        _pictures.Next.Enqueue((PictureOutcome.Unavailable, []));
        _pictures.Next.Enqueue((PictureOutcome.Picture, Jpeg(1)));
        _pictures.Next.Enqueue((PictureOutcome.Unavailable, []));

        for (var i = 0; i < 4; i++)
        {
            await sender.StepAsync(CancellationToken.None);
        }

        Assert.Equal([[], Jpeg(1), []], _sent);
    }

    [Fact]
    public async Task ACaptureThatCannotBeMade_ReadsAsNotAvailable_IsLoggedOnce_AndIsTriedAgain()
    {
        var attempts = 0;
        _pictures.Next.Enqueue((PictureOutcome.Picture, Jpeg(7)));
        var sender = Sender(open: () => ++attempts < 3 ? throw new InvalidOperationException("no duplication") : _pictures);

        for (var i = 0; i < 3; i++)
        {
            await sender.StepAsync(CancellationToken.None);
        }

        Assert.Equal(3, attempts);
        Assert.Equal([[], Jpeg(7)], _sent);
        Assert.Equal(["The screen could not be captured: no duplication"], _log);
    }

    [Fact]
    public async Task ACaptureThatFails_IsClosed_AndOpenedAgainNextTurn()
    {
        var second = new FakePictures();
        second.Next.Enqueue((PictureOutcome.Picture, Jpeg(2)));
        var opened = 0;
        var sender = Sender(open: () => ++opened == 1 ? _pictures : second);
        _pictures.Throws = new InvalidOperationException("device removed");

        await sender.StepAsync(CancellationToken.None);
        await sender.StepAsync(CancellationToken.None);

        Assert.True(_pictures.Disposed);
        Assert.Equal([[], Jpeg(2)], _sent);
    }

    [Fact]
    public async Task AtMostOnePictureASecond()
    {
        var clock = new ManualTimeProvider();
        for (byte i = 0; i < 10; i++)
        {
            _pictures.Next.Enqueue((PictureOutcome.Picture, Jpeg(i)));
        }

        using var stop = new CancellationTokenSource();
        var run = Sender(time: clock).RunAsync(stop.Token);

        await Eventually(() => _sent.Count == 1 && clock.PendingTimers == 1, "the first picture, then the wait");
        clock.Advance(TimeSpan.FromMilliseconds(999));
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.Single(_sent);

        clock.Advance(TimeSpan.FromMilliseconds(1));
        await Eventually(() => _sent.Count == 2, "the second picture a second later");

        await stop.CancelAsync();
        await run;
        Assert.True(_pictures.Disposed);
    }

    [Fact]
    public async Task NeverASecondPicture_WhileOneIsInFlight()
    {
        var clock = new ManualTimeProvider();
        _pictures.Next.Enqueue((PictureOutcome.Picture, Jpeg(1)));
        _pictures.Next.Enqueue((PictureOutcome.Picture, Jpeg(2)));
        var arrived = new TaskCompletionSource();
        using var stop = new CancellationTokenSource();
        var run = Sender(
            send: async (jpeg, ct) =>
            {
                _sent.Enqueue(jpeg);
                await arrived.Task.WaitAsync(ct);
            },
            time: clock).RunAsync(stop.Token);

        await Eventually(() => _sent.Count == 1, "the first picture");
        clock.Advance(TimeSpan.FromSeconds(5));
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.Equal(1, _pictures.Takes);

        arrived.SetResult();
        await Eventually(() => _sent.Count == 2, "the second picture once the first arrived");

        await stop.CancelAsync();
        await run;
    }

    [Fact]
    public async Task ASendThatFails_EndsTheSender_Quietly()
    {
        _pictures.Next.Enqueue((PictureOutcome.Picture, Jpeg(1)));

        await Sender(send: (_, _) => throw new System.Net.Quic.QuicException(System.Net.Quic.QuicError.ConnectionAborted, null, "gone"))
            .RunAsync(CancellationToken.None);

        Assert.True(_pictures.Disposed);
        Assert.Empty(_log);
    }

    [Fact]
    public async Task ASendThatFailsForAnotherReason_EndsTheSender_AndIsLoggedOnce()
    {
        _pictures.Next.Enqueue((PictureOutcome.Picture, Jpeg(1)));

        await Sender(send: (_, _) => throw new InvalidOperationException("boom")).RunAsync(CancellationToken.None);

        Assert.True(_pictures.Disposed);
        Assert.Equal(["Screen pictures stopped: boom"], _log);
    }

    [Fact]
    public async Task ACaptureThatCannotBeReleased_DoesNotMakeRunAsyncThrow_AndIsLoggedOnce()
    {
        _pictures.Next.Enqueue((PictureOutcome.Picture, Jpeg(1)));
        _pictures.DisposeThrows = new InvalidOperationException("stuck");

        await Sender(send: (_, _) => throw new InvalidOperationException("boom")).RunAsync(CancellationToken.None);

        Assert.True(_pictures.Disposed);
        Assert.Equal(["Screen pictures stopped: boom", "The screen capture could not be released: stuck"], _log);
    }
}
