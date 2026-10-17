using System.Diagnostics;
using System.Threading.Channels;
using Promin.Master.Core.Rooms;
using Promin.Master.Core.Wall;

namespace Promin.Master.Core.Tests.Wall;

public sealed class RoomConnectionsTests
{
    private sealed class FakeConnection : IPcConnection
    {
        private readonly Channel<byte[]> _pictures = Channel.CreateUnbounded<byte[]>();

        public bool Disposed { get; private set; }
        public bool NeverCloses { get; init; }

        public void Send(byte[] picture) => _pictures.Writer.TryWrite(picture);

        public void End() => _pictures.Writer.TryComplete();

        public ValueTask<byte[]> ReceivePictureAsync(CancellationToken ct) => _pictures.Reader.ReadAsync(ct);

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            _pictures.Writer.TryComplete();
            return NeverCloses ? new ValueTask(new TaskCompletionSource().Task) : ValueTask.CompletedTask;
        }
    }

    private DateTimeOffset _now = new(2026, 10, 15, 9, 0, 0, TimeSpan.Zero);
    private readonly List<RoomMember> _dials = [];
    private readonly List<(string Id, Exception Failure)> _failures = [];
    private readonly List<string> _ended = [];
    private readonly List<(string Id, byte[] Picture)> _pictures = [];
    private Func<RoomMember, Task<FakeConnection>> _answer = _ => Task.FromResult(new FakeConnection());

    private RoomConnections<FakeConnection> Build()
    {
        var connections = new RoomConnections<FakeConnection>(
            (pc, _) =>
            {
                lock (_dials)
                {
                    _dials.Add(pc);
                }

                return _answer(pc);
            },
            () => _now,
            (id, failure) => _failures.Add((id, failure)));
        connections.Ended += id =>
        {
            lock (_ended)
            {
                _ended.Add(id);
            }
        };
        connections.PictureArrived += (id, picture) =>
        {
            lock (_pictures)
            {
                _pictures.Add((id, picture));
            }
        };
        return connections;
    }

    private static RoomMember Pc(string address = "192.168.1.21") =>
        new("device-01", "PC-01", address, 17843, new DateOnly(2026, 9, 22), true);

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
    public async Task Want_DialsOnce_WhileThePcIsBeingDialled()
    {
        var pending = new TaskCompletionSource<FakeConnection>();
        _answer = _ => pending.Task;
        await using var connections = Build();

        connections.Want(Pc());
        connections.Want(Pc());

        Assert.Single(_dials);
    }

    [Fact]
    public async Task Want_DoesNotDial_APcThatIsConnected()
    {
        await using var connections = Build();

        connections.Want(Pc());
        _now += TimeSpan.FromMinutes(1);
        connections.Want(Pc());

        Assert.Single(_dials);
    }

    // Review focus 2: a PC that refuses is tried every 5 s, and the log hears about it once.
    [Fact]
    public async Task AFailedDial_IsRetriedEveryFiveSeconds_AndIsReportedOnce_HoweverOftenItFails()
    {
        _answer = _ => Task.FromException<FakeConnection>(new InvalidOperationException("refused"));
        await using var connections = Build();

        connections.Want(Pc());
        _now += TimeSpan.FromSeconds(4);
        connections.Want(Pc());
        Assert.Single(_dials);

        for (var i = 0; i < 5; i++)
        {
            _now += TimeSpan.FromSeconds(5);
            connections.Want(Pc());
        }

        Assert.Equal(6, _dials.Count);
        Assert.Equal("refused", Assert.Single(_failures).Failure.Message);
    }

    [Fact]
    public async Task AFailureAfterASuccess_IsReportedAgain()
    {
        var connection = new FakeConnection();
        var answers = new Queue<Func<Task<FakeConnection>>>([
            () => Task.FromException<FakeConnection>(new InvalidOperationException("first")),
            () => Task.FromResult(connection),
            () => Task.FromException<FakeConnection>(new InvalidOperationException("again")),
        ]);
        _answer = _ => answers.Dequeue()();
        await using var connections = Build();

        connections.Want(Pc());
        _now += TimeSpan.FromSeconds(5);
        connections.Want(Pc());
        connection.End();
        await Eventually(() => _ended.Count == 1, "the connection ended");
        _now += TimeSpan.FromSeconds(5);
        connections.Want(Pc());

        Assert.Equal(["first", "again"], _failures.Select(f => f.Failure.Message));
    }

    [Fact]
    public async Task AConnectionThatEnds_IsReportedEnded_Closed_AndDialledAgainAtTheNewAddress()
    {
        var first = new FakeConnection();
        _answer = _ => Task.FromResult(first);
        await using var connections = Build();
        connections.Want(Pc("192.168.1.21"));

        first.End();
        await Eventually(() => _ended.Count == 1, "the end reported");
        Assert.True(first.Disposed);

        _answer = _ => Task.FromResult(new FakeConnection());
        _now += RoomConnections<FakeConnection>.DialGap;
        connections.Want(Pc("192.168.1.99"));

        Assert.Equal(["192.168.1.21", "192.168.1.99"], _dials.Select(d => d.LastAddress));
    }

    [Fact]
    public async Task Pictures_AreRelayed_WithThePcsId_AnEmptyOneToo()
    {
        var connection = new FakeConnection();
        _answer = _ => Task.FromResult(connection);
        await using var connections = Build();
        connections.Want(Pc());

        connection.Send([0xFF, 0xD8, 1]);
        connection.Send([]);

        await Eventually(() => _pictures.Count == 2, "two pictures");
        Assert.Equal("device-01", _pictures[0].Id);
        Assert.Equal([0xFF, 0xD8, 1], _pictures[0].Picture);
        Assert.Empty(_pictures[1].Picture);
    }

    [Fact]
    public async Task Drop_ClosesTheConnection_RaisesEnded_AndTheNextWantDialsAtOnce()
    {
        var connection = new FakeConnection();
        _answer = _ => Task.FromResult(connection);
        await using var connections = Build();
        connections.Want(Pc());

        connections.Drop("device-01");

        Assert.True(connection.Disposed);
        Assert.Equal(["device-01"], _ended);
        _answer = _ => Task.FromResult(new FakeConnection());
        connections.Want(Pc());
        Assert.Equal(2, _dials.Count);
        await Task.Delay(50);
        Assert.Single(_ended);   // the dropped connection's read loop does not report it a second time
    }

    [Fact]
    public async Task ADialThatFinishesAfterDrop_IsClosed_NotKept()
    {
        var pending = new TaskCompletionSource<FakeConnection>();
        _answer = _ => pending.Task;
        await using var connections = Build();
        connections.Want(Pc());

        connections.Drop("device-01");
        var late = new FakeConnection();
        pending.SetResult(late);

        await Eventually(() => late.Disposed, "the late connection closed");
        var fresh = new FakeConnection();
        _answer = _ => Task.FromResult(fresh);
        Assert.Same(fresh, await connections.GetAsync(Pc(), CancellationToken.None));
    }

    [Fact]
    public async Task GetAsync_ReturnsTheOpenConnection_WithoutDialling()
    {
        var connection = new FakeConnection();
        _answer = _ => Task.FromResult(connection);
        await using var connections = Build();
        connections.Want(Pc());

        Assert.Same(connection, await connections.GetAsync(Pc(), CancellationToken.None));
        Assert.Single(_dials);
    }

    [Fact]
    public async Task GetAsync_DialsAtOnce_EvenWithinTheGap()
    {
        _answer = _ => Task.FromException<FakeConnection>(new InvalidOperationException("refused"));
        await using var connections = Build();
        connections.Want(Pc());

        var connection = new FakeConnection();
        _answer = _ => Task.FromResult(connection);

        Assert.Same(connection, await connections.GetAsync(Pc(), CancellationToken.None));
        Assert.Equal(2, _dials.Count);
    }

    [Fact]
    public async Task GetAsync_DuringADial_WaitsForThatDial()
    {
        var pending = new TaskCompletionSource<FakeConnection>();
        _answer = _ => pending.Task;
        await using var connections = Build();
        connections.Want(Pc());

        var got = connections.GetAsync(Pc(), CancellationToken.None);
        var connection = new FakeConnection();
        pending.SetResult(connection);

        Assert.Same(connection, await got);
        Assert.Single(_dials);
    }

    [Fact]
    public async Task GetAsync_IsNull_WhenThePcCannotBeReached()
    {
        _answer = _ => Task.FromException<FakeConnection>(new InvalidOperationException("refused"));
        await using var connections = Build();

        Assert.Null(await connections.GetAsync(Pc(), CancellationToken.None));
    }

    // Review focus 5: closing the console with a PC gone silent must not hang.
    [Fact(Timeout = 10000)]
    public async Task DisposeAsync_ClosesEveryConnection_WithinASecond_EvenOneThatNeverAnswers()
    {
        var silent = new FakeConnection { NeverCloses = true };
        _answer = _ => Task.FromResult(silent);
        var connections = Build();
        connections.Want(Pc());

        var clock = Stopwatch.StartNew();
        await connections.DisposeAsync();

        Assert.True(silent.Disposed);
        Assert.InRange(clock.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(2));
        connections.Want(Pc());
        Assert.Single(_dials);   // nothing is dialled once closed
    }
}
