using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Educast.Protocol.Transport;

namespace Educast.Agent.Tests;

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
                });
        }

        public AgentPaths Paths { get; }
        public AgentLog Log { get; }
        public AgentServices Services { get; set; }
        public FakeListener Listener { get; } = new();
        public FakeBeacon Beacon { get; } = new();
        public bool BeaconStarted { get; private set; }
        public CancellationTokenSource Stop => _stop;

        public string LogText => File.Exists(Paths.LogFile) ? File.ReadAllText(Paths.LogFile) : "";

        public Task<int> RunAsync() =>
            AgentHost.RunAsync(AgentCommandLine.Parse([]), Paths, Log, Services, _stop.Token);

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
            await Task.Delay(10);
        }

        Assert.Contains("Listening on", rig.LogText);
        Assert.False(running.IsCompleted); // it runs until it is told to stop

        await rig.Stop.CancelAsync();
        var exitCode = await running.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(0, exitCode);
        Assert.True(rig.Listener.Disposed);
        Assert.True(rig.Beacon.Disposed);
        Assert.Contains("Educast agent stopping", rig.LogText);
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
            await Task.Delay(10);
        }

        await rig.Stop.CancelAsync();
        await running.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(["listener started", "beacon started"], order);
    }
}
