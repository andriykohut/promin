using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Promin.Protocol.Transport;

namespace Promin.Agent.Tests;

/// <summary>
/// Every way the agent can fail to start, and the exit code and log line for each, reached with fakes: nothing
/// here binds a port, makes a key, or starts the program.
/// </summary>
public class AgentHostTests
{
    private sealed class FakeListener : IAgentListener
    {
        public Func<CancellationToken, Task> OnStart { get; set; } = _ => Task.CompletedTask;
        public Func<CancellationToken, Task<QuicChannel>> OnAccept { get; set; } = WaitForCancellation<QuicChannel>;
        public bool Disposed { get; private set; }

        public IPEndPoint LocalEndPoint { get; } = new(IPAddress.Any, 17843);

        public Task StartAsync(CancellationToken ct) => OnStart(ct);

        public Task<QuicChannel> AcceptAsync(CancellationToken ct) => OnAccept(ct);

        public Task<PairingOutcome> AcceptPairingAsync(byte[] roomCode, CancellationToken ct, PairingAdmission admission) =>
            WaitForCancellation<PairingOutcome>(ct);

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeBeacon : IDisposable
    {
        public bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;
    }

    private sealed class Rig : IDisposable
    {
        private readonly TempDirectory _temp = new();
        private readonly CancellationTokenSource _stop = new(TimeSpan.FromSeconds(20));

        public Rig()
        {
            Paths = _temp.Paths;
            Log = new AgentLog(Paths.LogFile);
            Services = new AgentServices(
                () => { },
                _ => new DeviceIdentity(new string('a', 32), SelfSignedCertificateFactory.Create(new string('a', 32))),
                (_, _, _) => Listener,
                _ =>
                {
                    BeaconStarted = true;
                    return Beacon;
                },
                Time: Time);
        }

        public AgentPaths Paths { get; }
        public AgentLog Log { get; }
        public AgentServices Services { get; set; }
        public ManualTimeProvider Time { get; } = new();
        public FakeListener Listener { get; } = new();
        public FakeBeacon Beacon { get; } = new();
        public bool BeaconStarted { get; private set; }
        public CancellationTokenSource Stop => _stop;

        public string LogText => File.Exists(Paths.LogFile) ? File.ReadAllText(Paths.LogFile) : "";

        /// <summary>Gives the agent a lock whose calls go in <paramref name="calls"/>, on a PC locked with <paramref name="message"/>, or not locked when it is null.</summary>
        public void UseFakeLock(List<string> calls, string? message = "Break", FakeScreen? screen = null)
        {
            if (message is not null)
            {
                new LockFile(Paths.LockFile, _ => { }).Write(message);
            }

            Services = Services with
            {
                OpenScreenLock = (paths, log) =>
                {
                    var lockLog = new LockLog(log.Write);
                    return new ScreenLock(new LockFile(paths.LockFile, lockLog.Write), screen ?? new FakeScreen(calls), new FakeInput(calls), lockLog);
                },
            };
        }

        public void FailTheStart() =>
            Services = Services with { EnsureQuicSupported = () => throw new PlatformNotSupportedException("no msquic") };

        public Task<int> RunAsync(params string[] args) =>
            AgentHost.RunAsync(AgentCommandLine.Parse(args), Paths, Log, Services, _stop.Token);

        public void Dispose()
        {
            _stop.Dispose();
            _temp.Dispose();
        }
    }

    private static async Task<T> WaitForCancellation<T>(CancellationToken ct)
    {
        await Task.Delay(Timeout.Infinite, ct);
        throw new InvalidOperationException("unreachable");
    }

    [Fact]
    public async Task WhenQuicIsNotAvailable_ItExitsWithThree_AndSaysWindows11()
    {
        using var rig = new Rig();
        rig.Services = rig.Services with { EnsureQuicSupported = () => throw new PlatformNotSupportedException("no msquic") };

        var exitCode = await rig.RunAsync();

        Assert.Equal(ExitCodes.QuicUnavailable, exitCode);
        Assert.Equal(3, exitCode);
        Assert.Contains("Windows 11", rig.LogText);
        Assert.Contains("no msquic", rig.LogText);
        Assert.False(rig.BeaconStarted);
    }

    [Theory]
    [InlineData(typeof(IOException))]
    [InlineData(typeof(UnauthorizedAccessException))]
    [InlineData(typeof(CryptographicException))]
    public async Task WhenTheIdentityCannotBeLoadedOrCreated_ItExitsWithFive(Type failure)
    {
        using var rig = new Rig();
        rig.Services = rig.Services with
        {
            LoadIdentity = _ => throw (Exception)Activator.CreateInstance(failure, "the key store said no")!,
        };

        var exitCode = await rig.RunAsync();

        Assert.Equal(5, exitCode);
        Assert.Contains("identity could not be loaded or created", rig.LogText);
        Assert.Contains("the key store said no", rig.LogText);
    }

    [Fact]
    public async Task WhenThePinnedMastersFileIsUnreadable_ItExitsWithFive_RatherThanOpeningPairingToAnyone()
    {
        using var rig = new Rig();
        Directory.CreateDirectory(rig.Paths.AgentDirectory);
        File.WriteAllText(rig.Paths.MastersFile, "this is not json");

        var exitCode = await rig.RunAsync();

        Assert.Equal(5, exitCode);
        Assert.Contains("--reset-pairing", rig.LogText);
        Assert.False(rig.BeaconStarted);
    }

    [Fact]
    public async Task WhenTheListenersOwnCertificateCannotHandshake_ItExitsWithFour_AndSaysSo()
    {
        using var rig = new Rig();
        rig.Listener.OnStart = _ => throw new LocalCredentialException("could not complete", new CryptographicException("no key"));

        var exitCode = await rig.RunAsync();

        Assert.Equal(4, exitCode);
        Assert.Contains("own certificate cannot complete a TLS handshake", rig.LogText);
        Assert.Contains("no key", rig.LogText);
        Assert.False(rig.BeaconStarted); // a PC that cannot be connected to does not announce itself
        Assert.True(rig.Listener.Disposed);
    }

    [Fact]
    public async Task WhenThePortCannotBeBound_ItExitsWithFour_NamesThePort_AndDoesNotAnnounce()
    {
        using var rig = new Rig();
        rig.Listener.OnStart = _ => throw new SocketException((int)SocketError.AddressAlreadyInUse);

        var exitCode = await rig.RunAsync();

        Assert.Equal(4, exitCode);
        Assert.Contains("Could not listen on UDP port 17843", rig.LogText);
        Assert.Contains("SocketException", rig.LogText);
        Assert.False(rig.BeaconStarted);
        Assert.True(rig.Listener.Disposed);
    }

    [Fact]
    public async Task WhateverElseGoesWrongAtStart_ItExitsWithOne_AndLogsTheException()
    {
        using var rig = new Rig();
        rig.Services = rig.Services with { LoadIdentity = _ => throw new InvalidOperationException("nobody planned for this") };

        var exitCode = await rig.RunAsync();

        Assert.Equal(1, exitCode);
        Assert.Contains("unexpected error", rig.LogText);
        Assert.Contains("nobody planned for this", rig.LogText);
    }

    [Fact]
    public async Task WhenALoopFails_ItExitsWithOne_ReleasesWhatItHeld_AndLogsWhy()
    {
        using var rig = new Rig();
        rig.Listener.OnAccept = _ => throw new InvalidOperationException("the accept queue broke");

        var exitCode = await rig.RunAsync();

        Assert.Equal(1, exitCode);
        Assert.Contains("a loop failed", rig.LogText);
        Assert.Contains("the accept queue broke", rig.LogText);
        Assert.True(rig.Listener.Disposed);
        Assert.True(rig.Beacon.Disposed);
    }

    [Fact]
    public async Task WhenItIsToldToStop_ItReturnsZero_AndReleasesTheListenerAndTheBeacon()
    {
        using var rig = new Rig();
        var running = rig.RunAsync();
        for (var i = 0; i < 400 && !rig.LogText.Contains("Announcing"); i++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Assert.Contains("Listening on", rig.LogText);
        Assert.False(running.IsCompleted); // it runs until it is told to stop

        await rig.Stop.CancelAsync();
        var exitCode = await running.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        Assert.True(rig.Listener.Disposed);
        Assert.True(rig.Beacon.Disposed);
        Assert.Contains("Promin agent stopping", rig.LogText);
    }

    [Fact]
    public async Task TheListenerIsStartedBeforeThePcAnnouncesItself()
    {
        using var rig = new Rig();
        var order = new List<string>();
        rig.Listener.OnStart = _ =>
        {
            order.Add("listener started");
            return Task.CompletedTask;
        };
        var services = rig.Services;
        rig.Services = services with
        {
            StartBeacon = identity =>
            {
                order.Add("beacon started");
                return services.StartBeacon(identity);
            },
        };

        var running = rig.RunAsync();
        for (var i = 0; i < 400 && order.Count < 2; i++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        await rig.Stop.CancelAsync();
        await running.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal(["listener started", "beacon started"], order);
    }

    [Fact]
    public async Task TheLockIsRestored_BeforeAnythingElse()
    {
        using var rig = new Rig();
        var calls = new List<string>();
        rig.UseFakeLock(calls);
        rig.Services = rig.Services with
        {
            EnsureQuicSupported = () =>
            {
                calls.Add("quic check");
                throw new PlatformNotSupportedException("no msquic");
            },
        };

        var running = rig.RunAsync();
        await WaitForTheHoldAsync(rig);

        Assert.Equal(["show:Break", "hold", "quic check"], calls);
        rig.Time.Advance(RestartBackoff.Longest);
        await running.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task AFailedStart_OnALockedPc_HoldsTheLockForTheLongestBackoff_ThenReleases()
    {
        using var rig = new Rig();
        var calls = new List<string>();
        rig.UseFakeLock(calls);
        rig.FailTheStart();

        var running = rig.RunAsync();
        await WaitForTheHoldAsync(rig);
        rig.Time.Advance(RestartBackoff.Longest - TimeSpan.FromSeconds(1));

        Assert.False(running.IsCompleted);
        Assert.Equal(["show:Break", "hold"], calls);
        Assert.Contains("keeps the lock", rig.LogText);

        rig.Time.Advance(TimeSpan.FromSeconds(1));
        var exitCode = await running.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal(ExitCodes.QuicUnavailable, exitCode);
        Assert.Equal(["show:Break", "hold", "hide", "release"], calls);
        Assert.Equal("Break", new LockFile(rig.Paths.LockFile, _ => { }).Read());
    }

    [Fact]
    public async Task AnUnexpectedFailure_OnALockedPc_HoldsTheLockToo()
    {
        using var rig = new Rig();
        var calls = new List<string>();
        rig.UseFakeLock(calls);
        rig.Services = rig.Services with { LoadIdentity = _ => throw new InvalidOperationException("nobody planned for this") };

        var running = rig.RunAsync();
        await WaitForTheHoldAsync(rig);

        Assert.False(running.IsCompleted);
        Assert.Equal(["show:Break", "hold"], calls);

        rig.Time.Advance(RestartBackoff.Longest);
        Assert.Equal(ExitCodes.Unexpected, await running.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AFailedStart_OnAnUnlockedPc_ExitsAtOnce()
    {
        using var rig = new Rig();
        var calls = new List<string>();
        rig.UseFakeLock(calls, message: null);
        rig.FailTheStart();

        var exitCode = await rig.RunAsync().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal(ExitCodes.QuicUnavailable, exitCode);
        Assert.Equal(0, rig.Time.PendingTimers);
        Assert.DoesNotContain("keeps the lock", rig.LogText);
    }

    [Fact]
    public async Task TheNormalExit_OnALockedPc_DoesNotWait()
    {
        using var rig = new Rig();
        rig.UseFakeLock([]);
        var running = rig.RunAsync();
        for (var i = 0; i < 400 && !rig.LogText.Contains("Announcing"); i++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        await rig.Stop.CancelAsync();
        var exitCode = await running.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.DoesNotContain("keeps the lock", rig.LogText);
    }

    [Fact]
    public async Task BeingToldToStop_EndsTheHoldAtOnce()
    {
        using var rig = new Rig();
        rig.UseFakeLock([]);
        rig.FailTheStart();
        var running = rig.RunAsync();
        await WaitForTheHoldAsync(rig);

        await rig.Stop.CancelAsync();

        Assert.Equal(
            ExitCodes.QuicUnavailable,
            await running.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
    }

    private static async Task WaitForTheHoldAsync(Rig rig)
    {
        for (var i = 0; i < 400 && rig.Time.PendingTimers == 0; i++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task ARestoreThatFailed_IsTriedAgainEveryFiveSeconds_WithoutHoldingUpTheStart()
    {
        using var rig = new Rig();
        var calls = new List<string>();
        var screen = new FakeScreen(calls) { CanShow = false };
        rig.UseFakeLock(calls, screen: screen);

        var running = rig.RunAsync();
        await WaitForAsync(() => rig.LogText.Contains("Announcing") && rig.Time.PendingTimers > 0);
        Assert.Equal(["show:Break"], calls);

        rig.Time.Advance(TimeSpan.FromSeconds(4));
        Assert.Equal(["show:Break"], calls);

        rig.Time.Advance(TimeSpan.FromSeconds(1));
        await WaitForAsync(() => calls.Count == 2 && rig.Time.PendingTimers > 0);
        Assert.Equal(["show:Break", "show:Break"], calls);

        screen.CanShow = true;
        rig.Time.Advance(TimeSpan.FromSeconds(5));
        await WaitForAsync(() => calls.Count == 4);
        Assert.Equal(["show:Break", "show:Break", "show:Break", "hold"], calls);

        await rig.Stop.CancelAsync();
        await running.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task TheRetries_EndWithTheAgent()
    {
        using var rig = new Rig();
        var calls = new List<string>();
        rig.UseFakeLock(calls, screen: new FakeScreen(calls) { CanShow = false });
        var running = rig.RunAsync();
        await WaitForAsync(() => rig.Time.PendingTimers > 0);
        Assert.Equal(1, rig.Time.PendingTimers);

        await rig.Stop.CancelAsync();
        await running.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal(0, rig.Time.PendingTimers);
    }

    [Fact]
    public async Task ARetryUnderWay_EndsBeforeTheLockIsDisposed()
    {
        using var rig = new Rig();
        var calls = new List<string>();
        var screen = new FakeScreen(calls) { CanShow = false };
        rig.UseFakeLock(calls, screen: screen);
        var running = rig.RunAsync();
        await WaitForAsync(() => rig.Time.PendingTimers > 0);

        using var finish = new ManualResetEventSlim();
        var inShow = new TaskCompletionSource();
        screen.OnShow = () =>
        {
            inShow.TrySetResult();
            finish.Wait(TimeSpan.FromSeconds(10));
        };

        // On a thread of its own: the retry can run inside Advance, which would then not come back.
        _ = Task.Run(() => rig.Time.Advance(TimeSpan.FromSeconds(5)), CancellationToken.None);
        await inShow.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await rig.Stop.CancelAsync();
        await Task.Delay(100, TestContext.Current.CancellationToken);

        Assert.False(running.IsCompleted);
        Assert.False(screen.Disposed);

        finish.Set();
        await running.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.True(screen.Disposed);
    }

    [Fact]
    public async Task AWindowedAgent_TriesNoRestore()
    {
        using var rig = new Rig();
        rig.UseFakeLock([]);
        var running = rig.RunAsync("--windowed");
        await WaitForAsync(() => rig.LogText.Contains("Announcing"));
        await Task.Delay(100, TestContext.Current.CancellationToken);

        Assert.Equal(0, rig.Time.PendingTimers);

        await rig.Stop.CancelAsync();
        await running.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var i = 0; i < 400 && !condition(); i++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task AWindowedAgent_OpensNoLock()
    {
        using var rig = new Rig();
        var opened = false;
        rig.Services = rig.Services with
        {
            EnsureQuicSupported = () => throw new PlatformNotSupportedException("no msquic"),
            OpenScreenLock = (paths, log) =>
            {
                opened = true;
                return new ScreenLock(new LockFile(paths.LockFile, log.Write), new FakeScreen([]), new FakeInput([]), new LockLog(log.Write));
            },
        };

        await rig.RunAsync("--windowed");

        Assert.False(opened);
    }
}
