using System.Text;
using Promin.Protocol.Pairing;
using Promin.Protocol.Tests.TestDoubles;
using Promin.Protocol.Transport;

namespace Promin.Protocol.Tests.Transport;

/// <summary>
/// When an agent PC opens its pairing window, to whom, and how long it waits after a failure. Every wait the loop
/// makes is on a clock that moves only when a test says so, so no test waits out a backoff. The helpers do use real
/// time, in milliseconds: to see that the loop has reached a wait or opened a window, and (for "nothing more
/// happened") to give it 50 ms in which it could have.
/// </summary>
public class AgentPairingLoopTests
{
    private const string Thumbprint = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

    private sealed record WindowCall(string RoomCode, PairingAdmission Admission);

    /// <summary>A <see cref="ManualTimeProvider"/> that says when something started waiting on it.</summary>
    private sealed class WatchedClock : TimeProvider
    {
        private readonly ManualTimeProvider _inner = new();
        private int _timersCreated;

        public int TimersCreated => Volatile.Read(ref _timersCreated);

        public override long TimestampFrequency => _inner.TimestampFrequency;

        public override long GetTimestamp() => _inner.GetTimestamp();

        public override DateTimeOffset GetUtcNow() => _inner.GetUtcNow();

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = _inner.CreateTimer(callback, state, dueTime, period);
            Interlocked.Increment(ref _timersCreated);
            return timer;
        }

        public void Advance(TimeSpan by) => _inner.Advance(by);
    }

    private sealed class Rig : IAsyncDisposable
    {
        private readonly CancellationTokenSource _stop = new(TimeSpan.FromSeconds(20));
        private readonly Queue<Func<Task<PairingOutcome>>> _script = new();
        private readonly List<WindowCall> _calls = [];
        private readonly object _gate = new();
        private Task? _running;

        public Rig()
        {
            Clock = new WatchedClock();
            Pins = new PeerCertificateStore();
            Loop = new AgentPairingLoop(
                Pins,
                () => new RoomCodeLookup(RoomCode, RoomCodeProblem),
                (code, admission, ct) => OpenWindowAsync(code, admission),
                () =>
                {
                    Interlocked.Increment(ref SaveCalls);
                    if (SaveFails)
                    {
                        throw new IOException("the disk is full");
                    }
                },
                Log.Add,
                new PairingBackoff(),
                Clock);
        }

        public WatchedClock Clock { get; }
        public PeerCertificateStore Pins { get; }
        public AgentPairingLoop Loop { get; }
        public List<string> Log { get; } = [];
        public string? RoomCode { get; set; } = "482913";
        public string? RoomCodeProblem { get; set; }
        public bool SaveFails { get; set; }
        public int SaveCalls;

        public IReadOnlyList<WindowCall> Calls
        {
            get
            {
                lock (_gate)
                {
                    return [.. _calls];
                }
            }
        }

        public Rig Then(PairingOutcome outcome) => Then(() => Task.FromResult(outcome));

        public Rig Then(Func<Task<PairingOutcome>> next)
        {
            lock (_gate)
            {
                _script.Enqueue(next);
            }

            return this;
        }

        public Rig ThenNobodyDials(int times = 1)
        {
            for (var i = 0; i < times; i++)
            {
                Then(PairingOutcome.Failed(PairingFailure.NoMasterArrived, "nobody dialled"));
            }

            return this;
        }

        public Rig ThenWrongCode() =>
            Then(PairingOutcome.Failed(PairingFailure.KeyNotProven, "the master did not prove the room code"));

        public void Start() => _running = Loop.RunAsync(_stop.Token);

        private Task<PairingOutcome> OpenWindowAsync(byte[] code, PairingAdmission admission)
        {
            Func<Task<PairingOutcome>>? next;
            lock (_gate)
            {
                _calls.Add(new WindowCall(Encoding.ASCII.GetString(code), admission));
                _script.TryDequeue(out next);
            }

            // Out of script: wait for the next window, as a real one would, until the test ends.
            return next?.Invoke() ?? Task.Delay(Timeout.Infinite, _stop.Token).ContinueWith(
                _ => PairingOutcome.Failed(PairingFailure.NoMasterArrived, "ended"),
                TaskScheduler.Default);
        }

        /// <summary>Waits until the loop has opened this many windows in all.</summary>
        public Task WindowsOpenedAsync(int count) => WaitUntilAsync(() => Calls.Count >= count, $"{count} windows opened");

        /// <summary>Waits until the loop is waiting on the clock (this many waits have begun in all).</summary>
        public Task WaitingAsync(int waits) => WaitUntilAsync(() => Clock.TimersCreated >= waits, $"{waits} waits begun");

        private async Task WaitUntilAsync(Func<bool> condition, string what)
        {
            for (var i = 0; i < 2000 && !condition(); i++)
            {
                await Task.Delay(5);
            }

            Assert.True(condition(), $"timed out waiting for: {what}");
        }

        /// <summary>Asserts that nothing more happens for a while (real time, short): the loop is parked.</summary>
        public async Task AssertNoMoreWindowsAsync(int expected)
        {
            await Task.Delay(50);
            Assert.Equal(expected, Calls.Count);
        }

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

    private static PairingOutcome Paired(Rig rig)
    {
        rig.Pins.Pin("master-1", Thumbprint);
        return PairingOutcome.Succeeded("master-1", Thumbprint);
    }

    // ---- 1. No room code ------------------------------------------------------------------------

    [Fact]
    public async Task WithNoRoomCode_NoWindowIsOpened_AndItIsSaidOnce()
    {
        await using var rig = new Rig { RoomCode = null };

        rig.Start();
        await rig.WaitingAsync(1);
        rig.Clock.Advance(AgentPairingLoop.NoRoomCodeRecheck);
        await rig.WaitingAsync(2);
        rig.Clock.Advance(AgentPairingLoop.NoRoomCodeRecheck);
        await rig.WaitingAsync(3);

        Assert.Empty(rig.Calls);
        Assert.Equal([AgentPairingLoop.NoRoomCodeMessage], rig.Log);
        Assert.Equal(
            "No room code is set. Run \"Promin.Agent.exe --set-room-code <6 digits>\".",
            AgentPairingLoop.NoRoomCodeMessage);
    }

    [Fact]
    public async Task ARoomCodeFileThatIsThereAndCannotBeUsed_IsSaidOnce_InItsOwnWords_NotAsNoCodeSet()
    {
        await using var rig = new Rig { RoomCode = null, RoomCodeProblem = @"C:\ProgramData\Promin\agent.json exists but does not hold a six-digit room code." };

        rig.Start();
        await rig.WaitingAsync(1);
        rig.Clock.Advance(AgentPairingLoop.NoRoomCodeRecheck);
        await rig.WaitingAsync(2);

        Assert.Empty(rig.Calls);
        var line = Assert.Single(rig.Log);
        Assert.Contains("exists but does not hold a six-digit room code", line);
        Assert.Contains("--set-room-code", line);
        Assert.DoesNotContain("No room code is set", line);
    }

    [Fact]
    public async Task WhenTheProblemChanges_ItIsSaidAgain()
    {
        await using var rig = new Rig { RoomCode = null, RoomCodeProblem = "the file is not JSON." };
        rig.Start();
        await rig.WaitingAsync(1);

        rig.RoomCodeProblem = null; // the file was deleted: now it is simply not set
        rig.Clock.Advance(AgentPairingLoop.NoRoomCodeRecheck);
        await rig.WaitingAsync(2);

        Assert.Equal(2, rig.Log.Count);
        Assert.Contains("the file is not JSON", rig.Log[0]);
        Assert.Equal(AgentPairingLoop.NoRoomCodeMessage, rig.Log[1]);
    }

    [Fact]
    public async Task WhenTheCodeIsSetLater_TheNextCheckOpensAWindow_WithThatCode()
    {
        await using var rig = new Rig { RoomCode = null };
        rig.ThenNobodyDials();
        rig.Start();
        await rig.WaitingAsync(1);

        rig.RoomCode = "070707";
        rig.Clock.Advance(AgentPairingLoop.NoRoomCodeRecheck);
        await rig.WindowsOpenedAsync(1);

        Assert.Equal(new WindowCall("070707", PairingAdmission.Anyone), rig.Calls[0]);
    }

    [Fact]
    public async Task TheCodeIsReadAgainForEachWindow()
    {
        await using var rig = new Rig();
        var firstWindow = new TaskCompletionSource<PairingOutcome>();
        rig.Then(() => firstWindow.Task).ThenNobodyDials();
        rig.Start();
        await rig.WindowsOpenedAsync(1);

        rig.RoomCode = "111111";
        firstWindow.SetResult(PairingOutcome.Failed(PairingFailure.NoMasterArrived, "nobody dialled"));
        await rig.WindowsOpenedAsync(2);

        Assert.Equal(["482913", "111111"], rig.Calls.Take(2).Select(c => c.RoomCode));
    }

    // ---- 2. No console PC pinned: windows follow each other at once -----------------------------

    [Fact]
    public async Task WhenNobodyDials_TheNextWindowOpensAtOnce_WithNoWait()
    {
        await using var rig = new Rig();
        rig.ThenNobodyDials(5);

        rig.Start();
        await rig.WindowsOpenedAsync(5);

        Assert.All(rig.Calls, call => Assert.Equal(PairingAdmission.Anyone, call.Admission));
        Assert.Equal(0, rig.Clock.TimersCreated);

        // Five windows, one line: the state is said when it changes, not once a minute.
        Assert.Contains("any console PC", Assert.Single(rig.Log));
    }

    [Fact]
    public async Task EmptyWindows_DoNotRaiseTheBackoff()
    {
        await using var rig = new Rig();
        rig.ThenNobodyDials(3).ThenWrongCode();

        rig.Start();
        await rig.WaitingAsync(1);

        // After three empty windows the first failure still costs the first step, two seconds.
        Assert.Contains(rig.Log, line => line.Contains("in 2 s"));
    }

    // ---- 3. A peer that was let in and did not pair costs more each time -----------------------

    [Fact]
    public async Task EachWrongCode_WaitsLonger_BeforeTheNextWindow()
    {
        await using var rig = new Rig();
        rig.ThenWrongCode().ThenWrongCode().ThenWrongCode();
        rig.Start();

        await rig.WaitingAsync(1);
        await rig.AssertNoMoreWindowsAsync(1);
        rig.Clock.Advance(TimeSpan.FromSeconds(1.9));
        await rig.AssertNoMoreWindowsAsync(1); // two seconds have not passed
        rig.Clock.Advance(TimeSpan.FromSeconds(0.1));
        await rig.WindowsOpenedAsync(2);

        await rig.WaitingAsync(2);
        rig.Clock.Advance(TimeSpan.FromSeconds(3.9));
        await rig.AssertNoMoreWindowsAsync(2); // the second wait is four seconds
        rig.Clock.Advance(TimeSpan.FromSeconds(0.1));
        await rig.WindowsOpenedAsync(3);

        await rig.WaitingAsync(3);
        rig.Clock.Advance(TimeSpan.FromSeconds(7.9));
        await rig.AssertNoMoreWindowsAsync(3);
        rig.Clock.Advance(TimeSpan.FromSeconds(0.1));
        await rig.WindowsOpenedAsync(4);
    }

    /// <summary>
    /// A peer can run the first rounds of the exchange, find out from what the agent sends back whether its
    /// guess was right, and walk away. That is a guess like any other, so every way a window ends after it let
    /// a peer in, without that peer pairing, costs the next step of the backoff.
    /// </summary>
    [Theory]
    [InlineData(PairingFailure.KeyNotProven)]
    [InlineData(PairingFailure.ConnectionLost)]
    [InlineData(PairingFailure.TimedOut)]
    [InlineData(PairingFailure.NotConfirmed)]
    [InlineData(PairingFailure.WindowSpent)]
    [InlineData(PairingFailure.PeerCertificateRefused)]
    public async Task AnyWindowThatLetAPeerInAndDidNotPair_CostsTheBackoff(PairingFailure failure)
    {
        await using var rig = new Rig();
        rig.Then(PairingOutcome.Failed(failure, "it did not pair"));
        rig.Start();

        await rig.WaitingAsync(1);
        await rig.AssertNoMoreWindowsAsync(1);
        rig.Clock.Advance(PairingBackoff.First);
        await rig.WindowsOpenedAsync(2);
    }

    [Fact]
    public async Task TheBackoffIsLogged_WithItsReason()
    {
        await using var rig = new Rig();
        rig.ThenWrongCode();

        rig.Start();
        await rig.WaitingAsync(1);

        var line = Assert.Single(rig.Log, l => l.Contains("did not pair"));
        Assert.Contains("did not pair", line);
        Assert.Contains("KeyNotProven", line);
        Assert.Contains("the master did not prove the room code", line);
        Assert.Contains("in 2 s", line);
    }

    [Fact]
    public async Task AWindowThatThrows_IsLogged_AndCostsTheBackoff_AndTheLoopCarriesOn()
    {
        await using var rig = new Rig();
        rig.Then(() => throw new InvalidOperationException("a window is already open"));
        rig.Start();

        await rig.WaitingAsync(1);
        Assert.Contains(rig.Log, line => line.Contains("a window is already open"));
        rig.Clock.Advance(PairingBackoff.First);
        await rig.WindowsOpenedAsync(2);
    }

    // ---- 4. Success -----------------------------------------------------------------------------

    [Fact]
    public async Task WhenAPeerPairs_ThePinIsSaved_AndFromThenOnOnlyPinnedPeersAreAdmitted()
    {
        await using var rig = new Rig();
        rig.Then(() => Task.FromResult(Paired(rig)));
        rig.Start();

        await rig.WindowsOpenedAsync(2);

        Assert.Equal(1, rig.SaveCalls);
        Assert.Equal(PairingAdmission.Anyone, rig.Calls[0].Admission);
        Assert.Equal(PairingAdmission.PinnedPeersOnly, rig.Calls[1].Admission);
        Assert.Contains(rig.Log, line => line.Contains("Paired with console PC master-1"));
    }

    [Fact]
    public async Task APairingResetsTheBackoff()
    {
        await using var rig = new Rig();
        rig.ThenWrongCode().ThenWrongCode();
        rig.Then(() => Task.FromResult(Paired(rig)));
        rig.ThenWrongCode();
        rig.Start();

        await rig.WaitingAsync(1);
        rig.Clock.Advance(TimeSpan.FromSeconds(2));
        await rig.WaitingAsync(2);
        rig.Clock.Advance(TimeSpan.FromSeconds(4));
        await rig.WindowsOpenedAsync(4); // after the success the next window opens at once, and fails
        await rig.WaitingAsync(3);

        // The third failure in all, but the first since the success: two seconds, not eight.
        rig.Clock.Advance(TimeSpan.FromSeconds(2));
        await rig.WindowsOpenedAsync(5);
    }

    [Fact]
    public async Task WhenSavingThePinFails_ItIsLogged_AndTheLoopCarriesOn()
    {
        await using var rig = new Rig { SaveFails = true };
        rig.Then(() => Task.FromResult(Paired(rig)));
        rig.Start();

        await rig.WindowsOpenedAsync(2);

        Assert.Contains(rig.Log, line => line.Contains("could not be saved") && line.Contains("the disk is full"));
        Assert.Equal(PairingAdmission.PinnedPeersOnly, rig.Calls[1].Admission);
    }

    // ---- 5. A master is already pinned at start -------------------------------------------------

    [Fact]
    public async Task WithAMasterAlreadyPinned_TheFirstWindowAdmitsPinnedPeersOnly()
    {
        await using var rig = new Rig();
        rig.Pins.Pin("master-1", Thumbprint);
        rig.ThenNobodyDials();

        rig.Start();
        await rig.WindowsOpenedAsync(1);

        Assert.Equal(PairingAdmission.PinnedPeersOnly, rig.Calls[0].Admission);
    }

    [Fact]
    public async Task WhenTheMasterIsUnpinned_AdmissionIsAnyoneAgain()
    {
        await using var rig = new Rig();
        rig.Pins.Pin("master-1", Thumbprint);
        var firstWindow = new TaskCompletionSource<PairingOutcome>();
        rig.Then(() => firstWindow.Task).ThenNobodyDials();
        rig.Start();
        await rig.WindowsOpenedAsync(1);

        rig.Pins.Unpin("master-1");
        firstWindow.SetResult(PairingOutcome.Failed(PairingFailure.NoMasterArrived, "nobody dialled"));
        await rig.WindowsOpenedAsync(2);

        Assert.Equal(PairingAdmission.Anyone, rig.Calls[1].Admission);
    }

    // ---- 6. Stopping ----------------------------------------------------------------------------

    [Fact]
    public async Task Cancelling_EndsTheLoop_WhetherItIsInAWindowOrWaiting()
    {
        var rig = new Rig();
        rig.ThenWrongCode();
        rig.Start();
        await rig.WaitingAsync(1);

        await rig.DisposeAsync(); // cancels, then waits for RunAsync to return

        var inWindow = new Rig();
        inWindow.Start();
        await inWindow.WindowsOpenedAsync(1);
        await inWindow.DisposeAsync();
    }
}
