using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Educast.Protocol;
using Educast.Protocol.Discovery;
using Educast.Protocol.Pairing;
using Educast.Protocol.Transport;

namespace Educast.Agent;

/// <summary>The listener as the agent uses it: what <see cref="QuicAgentListener"/> offers, so that start-up can be tested with a fake.</summary>
internal interface IAgentListener : IAsyncDisposable
{
    IPEndPoint LocalEndPoint { get; }

    Task StartAsync(CancellationToken ct);

    Task<QuicChannel> AcceptAsync(CancellationToken ct);

    Task<PairingOutcome> AcceptPairingAsync(byte[] roomCode, CancellationToken ct, PairingAdmission admission);
}

internal sealed class QuicAgentListenerAdapter(QuicAgentListener listener) : IAgentListener
{
    public IPEndPoint LocalEndPoint => listener.LocalEndPoint;

    public Task StartAsync(CancellationToken ct) => listener.StartAsync(ct);

    public Task<QuicChannel> AcceptAsync(CancellationToken ct) => listener.AcceptAsync(ct);

    public Task<PairingOutcome> AcceptPairingAsync(byte[] roomCode, CancellationToken ct, PairingAdmission admission) =>
        listener.AcceptPairingAsync(roomCode, ct, admission: admission);

    public ValueTask DisposeAsync() => listener.DisposeAsync();
}

/// <summary>
/// The parts of start-up that touch this machine (QUIC, the Windows key store, the network), as functions, so
/// that every way the agent can fail to start, and the exit code and log line for it, can be reached from a test.
/// </summary>
internal sealed record AgentServices(
    Action EnsureQuicSupported,
    Func<string, DeviceIdentity> LoadIdentity,
    Func<DeviceIdentity, PeerCertificateStore, Action<Exception>, IAgentListener> CreateListener,
    Func<DeviceIdentity, IDisposable> StartBeacon)
{
    public static AgentServices ForThisMachine() => new(
        QuicTransportSettings.EnsureSupported,
        DeviceIdentity.LoadOrCreate,
        (identity, masters, connectionRejected) => new QuicAgentListenerAdapter(
            new QuicAgentListener(
                identity.Certificate, masters, EducastPorts.Quic, bindAddress: null, connectionRejected: connectionRejected,
                inboundFrameQueueCapacity: LiveBounds.ReceiveQueueFrames)),
        identity =>
        {
            var beacon = new LanDiscoveryBeacon(
                identity.DeviceId,
                Environment.MachineName,
                EducastPorts.Quic,
                PeerCertificateStore.ComputeThumbprint(identity.Certificate),
                EducastPorts.Discovery);
            beacon.Start();
            return beacon;
        });
}

/// <summary>
/// The agent when it runs: its identity, the listener, the beacon that announces it, and the two loops side by
/// side over one listener (teacher PCs that are paired, and pairing windows). It returns the process's exit code
/// and says why in the log; nothing here ends the process itself. There is deliberately no <c>ProcessExit</c>
/// handler: it runs after <c>Main</c> has returned and everything it could cancel is already disposed. The agent
/// is ended by <c>Stop-Process</c> or, in a test, by the token.
/// </summary>
internal static class AgentHost
{
    /// <summary>Records what nobody catches (a crash on a thread, a task nobody awaited) in the log. Once, from <c>Main</c>.</summary>
    public static void LogWhatNobodyCatches(AgentLog log)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) => log.Write($"Unhandled exception: {e.ExceptionObject}");
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            log.Write($"A task failed and nobody was waiting for it: {e.Exception}");
            e.SetObserved();
        };
    }

    /// <summary>
    /// Runs until <paramref name="stop"/> is cancelled (a process normally runs until it is killed) and returns 0, or
    /// returns an <see cref="ExitCodes"/> value as soon as it cannot start or something fails.
    /// </summary>
    public static async Task<int> RunAsync(
        AgentCommandLine command, AgentPaths paths, AgentLog log, AgentServices services, CancellationToken stop)
    {
        log.Write($"Educast agent started ({(command.Windowed ? "windowed" : "full screen")}), process {Environment.ProcessId}.");
        try
        {
            return await RunCoreAsync(command, paths, log, services, stop).ConfigureAwait(false);
        }
        catch (Exception failure)
        {
            log.Write($"The agent stopped because of an unexpected error: {failure}");
            return ExitCodes.Unexpected;
        }
    }

    private static async Task<int> RunCoreAsync(
        AgentCommandLine command, AgentPaths paths, AgentLog log, AgentServices services, CancellationToken stop)
    {
        try
        {
            services.EnsureQuicSupported();
        }
        catch (PlatformNotSupportedException unavailable)
        {
            log.Write($"Educast needs Windows 11: QUIC is not available on this PC. {unavailable.Message}");
            return ExitCodes.QuicUnavailable;
        }

        DeviceIdentity identity;
        try
        {
            identity = services.LoadIdentity(paths.IdentityDirectory);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or CryptographicException)
        {
            log.Write($"This PC's identity could not be loaded or created in {paths.IdentityDirectory}: {e.Message}");
            return ExitCodes.FileUnusable;
        }

        using (identity)
        {
            var masters = new PeerCertificateStore();
            try
            {
                masters.Load(paths.MastersFile);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
            {
                // Starting with no pins would open pairing to anyone, so an unreadable file stops the agent instead.
                log.Write(
                    $"The list of paired teacher PCs ({paths.MastersFile}) could not be read: {e.Message} " +
                    "The agent will not start with it. To forget it and start again, run \"Educast.Agent.exe --reset-pairing\".");
                return ExitCodes.FileUnusable;
            }

            log.Write($"Device {identity.DeviceId} ({Environment.MachineName}); paired teacher PCs: {masters.Count}.");

            using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(stop);

            var refusals = new RefusalLog(log.Write, TimeSpan.FromMinutes(1));
            await using var listener = services.CreateListener(identity, masters, refusals.Record);
            try
            {
                await listener.StartAsync(shutdown.Token).ConfigureAwait(false);
            }
            catch (LocalCredentialException e)
            {
                log.Write($"This PC's own certificate cannot complete a TLS handshake, so no teacher PC could connect: {e.Message} ({e.InnerException?.Message})");
                return ExitCodes.ListenerFailed;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                log.Write($"Could not listen on UDP port {EducastPorts.Quic}: {e.GetType().Name}: {e.Message}");
                return ExitCodes.ListenerFailed;
            }

            log.Write($"Listening on {listener.LocalEndPoint}; this PC's certificate completed a handshake with itself.");

            using var beacon = services.StartBeacon(identity);
            log.Write($"Announcing this PC on the network (UDP port {EducastPorts.Discovery}).");

            var pairing = new AgentPairingLoop(
                masters,
                new AgentSettings(paths.RoomCodeFile).Lookup,
                (roomCode, admission, ct) => listener.AcceptPairingAsync(roomCode, ct, admission),
                () => masters.Save(paths.MastersFile),
                log.Write,
                new PairingBackoff(),
                TimeProvider.System);

            var connections = new NewestConnectionWins();
            var displays = new BroadcastDisplayFactory(command.Windowed, log.Write);

            var pairingLoop = pairing.RunAsync(shutdown.Token);
            var acceptLoop = AcceptTeacherPcsAsync(listener, connections, displays, log, shutdown.Token);

            // Neither loop ends by itself; one that does has failed.
            var ended = await Task.WhenAny(pairingLoop, acceptLoop).ConfigureAwait(false);
            await shutdown.CancelAsync().ConfigureAwait(false);
            await connections.StopAsync().ConfigureAwait(false);
            if (ended.IsFaulted)
            {
                log.Write($"The agent stopped because a loop failed: {ended.Exception}");
                return ExitCodes.Unexpected;
            }

            log.Write("Educast agent stopping.");
            return ExitCodes.Success;
        }
    }

    private static async Task AcceptTeacherPcsAsync(
        IAgentListener listener,
        NewestConnectionWins connections,
        IBroadcastDisplayFactory displays,
        AgentLog log,
        CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            QuicChannel channel;
            try
            {
                channel = await listener.AcceptAsync(ct).ConfigureAwait(false);
            }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException)
            {
                return;
            }

            var teacherPc = channel.PinnedDeviceId;
            log.Write($"Teacher PC {teacherPc} connected from {channel.RemoteEndPoint.Address}.");

            await connections.StartAsync(
                async token =>
                {
                    var connection = new QuicAgentConnection(channel);
                    try
                    {
                        await new BroadcastConnection(displays, log.Write).RunAsync(connection, token).ConfigureAwait(false);
                    }
                    finally
                    {
                        await connection.DisposeAsync().ConfigureAwait(false);
                        log.Write($"The connection from teacher PC {teacherPc} is closed.");
                    }
                },
                ct).ConfigureAwait(false);
        }
    }
}
