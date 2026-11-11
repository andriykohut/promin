using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Promin.MediaPipeline.Pictures;
using Promin.Protocol;
using Promin.Protocol.Discovery;
using Promin.Protocol.Pairing;
using Promin.Protocol.Transport;

namespace Promin.Agent;

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
/// <c>OpenScreenPictures</c> is null in tests, which then take no pictures; <c>Person</c> is null in tests, which then
/// never act as the signed-in person; <c>OpenScreenLock</c> is null in tests, which then never lock. <c>Time</c> is set
/// in tests, which then move the clock themselves, and null on a PC, which uses the system clock. <c>Remote</c> is null
/// in tests and on a windowed agent, which then refuse every remote stream.
/// </summary>
internal sealed record AgentServices(
    Action EnsureQuicSupported,
    Func<string, DeviceIdentity> LoadIdentity,
    Func<DeviceIdentity, PeerCertificateStore, Action<Exception>, IAgentListener> CreateListener,
    Func<DeviceIdentity, IDisposable> StartBeacon,
    Func<IScreenPictures>? OpenScreenPictures = null,
    Func<ISignedInPerson>? Person = null,
    Func<AgentPaths, AgentLog, ScreenLock>? OpenScreenLock = null,
    TimeProvider? Time = null,
    RemoteParts? Remote = null)
{
    public static AgentServices ForThisMachine(AgentLog log, bool windowed) => new(
        QuicTransportSettings.EnsureSupported,
        DeviceIdentity.LoadOrCreate,
        (identity, masters, connectionRejected) => new QuicAgentListenerAdapter(
            new QuicAgentListener(
                identity.Certificate, masters, ProminPorts.Quic, bindAddress: null, connectionRejected: connectionRejected,
                inboundFrameQueueCapacity: LiveBounds.ReceiveQueueFrames)),
        identity =>
        {
            var beacon = new LanDiscoveryBeacon(
                identity.DeviceId,
                Environment.MachineName,
                ProminPorts.Quic,
                PeerCertificateStore.ComputeThumbprint(identity.Certificate),
                ProminPorts.Discovery);
            beacon.Start();
            return beacon;
        },
        () => new ScreenPictures(),
        () => new WindowsSignedInPerson(Environment.ProcessPath!),
        (paths, log) =>
        {
            var lockLog = new LockLog(log.Write);
            return new ScreenLock(
                new LockFile(paths.LockFile, lockLog.Write), new LockScreenWindow(lockLog.Write), new InputBlock(lockLog.Write), lockLog);
        },
        Remote: windowed ? null : new RemoteParts(
            (send, nextId) => ControlVideo.Open(send, nextId, log.Write),
            () => new ControlTab(log.Write),
            () => new ClipboardWatch(log.Write),
            InputInjector.ForThisPc(),
            InputDesktop.IsDefault,
            TimeSpan.FromMilliseconds(500)));
}

/// <summary>
/// The agent when it runs: a lock the PC holds, restored before anything else, then its identity, the listener, the
/// beacon that announces it, and the two loops side by side over one listener (console PCs that are paired, each with
/// its broadcast, its screen pictures, its file sends and its lock stream, and pairing windows), with a third beside
/// them for a lock that could not be restored. A <c>--windowed</c> agent has no lock, so no third loop.
/// It returns the process's exit code and says why in the log, with one exception: a lock window or
/// input block that will not go ends the process there and then (<see cref="LockScreenWindow.Hide"/>,
/// <see cref="InputBlock.Release"/>), which an unlock or the lock's disposal can reach. There is deliberately no
/// <c>ProcessExit</c> handler: it runs after <c>Main</c> has returned and everything it could cancel is already
/// disposed. The agent is ended by <c>Stop-Process</c> or, in a test, by the token.
/// </summary>
internal static class AgentHost
{
    private static readonly TimeSpan RestoreEvery = TimeSpan.FromSeconds(5);

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
    /// returns an <see cref="ExitCodes"/> value as soon as it cannot start or something fails. On a locked PC such a
    /// failure first holds the lock for <see cref="RestartBackoff.Longest"/>, or until <paramref name="stop"/>.
    /// </summary>
    public static async Task<int> RunAsync(
        AgentCommandLine command, AgentPaths paths, AgentLog log, AgentServices services, CancellationToken stop)
    {
        log.Write($"Promin agent started ({(command.Windowed ? "windowed" : "full screen")}), process {Environment.ProcessId}.");
        using var screenLock = command.Windowed ? null : services.OpenScreenLock?.Invoke(paths, log);
        screenLock?.Restore();

        int exitCode;
        try
        {
            exitCode = await RunCoreAsync(command, paths, log, services, screenLock, stop).ConfigureAwait(false);
        }
        catch (Exception failure)
        {
            log.Write($"The agent stopped because of an unexpected error: {failure}");
            exitCode = ExitCodes.Unexpected;
        }

        if (exitCode != ExitCodes.Success && screenLock?.IsLocked == true)
        {
            // Exiting now would leave the PC free for the whole backoff, every time. A run this long counts as healthy, so
            // the service starts the agent again in 2 s: locked but for that, and a passing failure heals by itself.
            log.Write($"The agent failed on a locked PC, so it keeps the lock for {RestartBackoff.Longest.TotalSeconds:F0} seconds before it exits.");
            await Task.Delay(RestartBackoff.Longest, services.Time ?? TimeProvider.System, stop)
                .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }

        return exitCode;
    }

    private static async Task<int> RunCoreAsync(
        AgentCommandLine command, AgentPaths paths, AgentLog log, AgentServices services, ScreenLock? screenLock, CancellationToken stop)
    {
        try
        {
            services.EnsureQuicSupported();
        }
        catch (PlatformNotSupportedException unavailable)
        {
            log.Write($"Promin needs Windows 11: QUIC is not available on this PC. {unavailable.Message}");
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
                    $"The list of paired console PCs ({paths.MastersFile}) could not be read: {e.Message} " +
                    "The agent will not start with it. To forget it and start again, run \"Promin.Agent.exe --reset-pairing\".");
                return ExitCodes.FileUnusable;
            }

            log.Write($"Device {identity.DeviceId} ({Environment.MachineName}); paired console PCs: {masters.Count}.");

            using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(stop);

            var refusals = new RefusalLog(log.Write, TimeSpan.FromMinutes(1));
            await using var listener = services.CreateListener(identity, masters, refusals.Record);
            try
            {
                await listener.StartAsync(shutdown.Token).ConfigureAwait(false);
            }
            catch (LocalCredentialException e)
            {
                log.Write($"This PC's own certificate cannot complete a TLS handshake, so no console PC could connect: {e.Message} ({e.InnerException?.Message})");
                return ExitCodes.ListenerFailed;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                log.Write($"Could not listen on UDP port {ProminPorts.Quic}: {e.GetType().Name}: {e.Message}");
                return ExitCodes.ListenerFailed;
            }

            log.Write($"Listening on {listener.LocalEndPoint}; this PC's certificate completed a handshake with itself.");

            using var beacon = services.StartBeacon(identity);
            log.Write($"Announcing this PC on the network (UDP port {ProminPorts.Discovery}).");

            if (services.OpenScreenPictures is { } openPictures)
            {
                // Off the start-up path: a capture can take seconds to deliver its first picture. It is let go within five
                // seconds; a console PC that connects meanwhile has its first capture refused once and retried a second later.
                _ = Task.Run(() => log.Write(ScreenPictureProbe.Describe(openPictures)), CancellationToken.None);
            }

            if (services.Person is { } person)
            {
                // Off the start-up path: the helper it starts is given up to ten seconds to report.
                _ = Task.Run(
                    async () => log.Write(await SignedInPersonProbe.DescribeAsync(person()).ConfigureAwait(false)),
                    CancellationToken.None);
            }

            var pairing = new AgentPairingLoop(
                masters,
                new AgentSettings(paths.RoomCodeFile).Lookup,
                (roomCode, admission, ct) => listener.AcceptPairingAsync(roomCode, ct, admission),
                () => masters.Save(paths.MastersFile),
                log.Write,
                new PairingBackoff(),
                TimeProvider.System);

            var connections = new NewestConnectionWins();

            var pairingLoop = pairing.RunAsync(shutdown.Token);
            var acceptLoop = AcceptConsolePcsAsync(
                listener, connections, command.Windowed, services.OpenScreenPictures, services.Person, services.Remote, screenLock, log, shutdown.Token);
            var restoreLoop = screenLock is null
                ? Task.CompletedTask
                : RestoreLockAsync(screenLock, services.Time ?? TimeProvider.System, shutdown.Token);

            // Neither the pairing loop nor the accept loop ends by itself; one that does has failed.
            var ended = await Task.WhenAny(pairingLoop, acceptLoop).ConfigureAwait(false);
            await shutdown.CancelAsync().ConfigureAwait(false);
            await connections.StopAsync().ConfigureAwait(false);

            // The caller disposes screenLock once this returns.
            await restoreLoop.ConfigureAwait(false);
            if (ended.IsFaulted)
            {
                log.Write($"The agent stopped because a loop failed: {ended.Exception}");
                return ExitCodes.Unexpected;
            }

            log.Write("Promin agent stopping.");
            return ExitCodes.Success;
        }
    }

    /// <summary>
    /// Calls <see cref="ScreenLock.Restore"/> every <see cref="RestoreEvery"/> until <paramref name="ct"/> is cancelled,
    /// waiting first: the caller has already tried once. The first try can fail for a reason that passes, and the likeliest
    /// moment is sign-in on a slow PC.
    /// </summary>
    private static async Task RestoreLockAsync(ScreenLock screenLock, TimeProvider time, CancellationToken ct)
    {
        while (true)
        {
            await Task.Delay(RestoreEvery, time, ct).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            if (ct.IsCancellationRequested)
            {
                return;
            }

            screenLock.Restore();
        }
    }

    private static async Task AcceptConsolePcsAsync(
        IAgentListener listener,
        NewestConnectionWins connections,
        bool windowed,
        Func<IScreenPictures>? openScreenPictures,
        Func<ISignedInPerson>? person,
        RemoteParts? remote,
        ScreenLock? screenLock,
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

            var consolePc = channel.PinnedDeviceId;
            log.Write($"Console PC {consolePc} connected from {channel.RemoteEndPoint.Address}.");
            var onThisPc = ConsoleOnThisPc.Is(channel.RemoteEndPoint.Address, ConsoleOnThisPc.OwnAddresses());

            await connections.StartAsync(
                async token =>
                {
                    var connection = new QuicAgentConnection(channel);
                    await using var previews = new PreviewSwitch(
                        openScreenPictures is null
                            ? _ => Task.CompletedTask
                            : stopPictures => new PictureSender(
                                openScreenPictures,
                                (jpeg, sendCt) => channel.SendVideoFrameAsync(AgentStream.Tag(AgentStream.Picture, jpeg), sendCt),
                                log.Write,
                                TimeProvider.System).RunAsync(stopPictures));
                    using var stopFiles = CancellationTokenSource.CreateLinkedTokenSource(token);
                    var files = person is null
                        ? Task.CompletedTask
                        : new FileReceiver(person, log.Write, () => screenLock?.IsLocked == true).RunAsync(connection, stopFiles.Token);
                    using var stopLockLink = CancellationTokenSource.CreateLinkedTokenSource(token);
                    var lockLink = screenLock is null
                        ? Task.CompletedTask
                        : new LockLink(screenLock, mayLock: !onThisPc, log.Write).RunAsync(connection, stopLockLink.Token);
                    using var stopRemote = CancellationTokenSource.CreateLinkedTokenSource(token);
                    var remoteLink = new RemoteLink(remote, screenLock, previews, mayControl: !onThisPc, log.Write)
                        .RunAsync(connection, (frame, sendCt) => channel.SendVideoFrameAsync(frame, sendCt), stopRemote.Token);

                    // A step of closing that throws is logged by type only, and the next one still runs: each lets go of
                    // something the next connection needs.
                    async Task StepAsync(Func<Task> step)
                    {
                        try
                        {
                            await step().ConfigureAwait(false);
                        }
                        catch (Exception e)
                        {
                            log.Write($"A step of closing the connection from console PC {consolePc} failed: {e.GetType().Name}.");
                        }
                    }

                    try
                    {
                        await new BroadcastConnection(
                            new BroadcastDisplayFactory(windowed, log.Write, onThisPc ? null : screenLock), log.Write).RunAsync(connection, token).ConfigureAwait(false);
                    }
                    finally
                    {
                        // Control ends first, so that the PC has its keyboard and mouse back before the connection goes.
                        await StepAsync(stopRemote.CancelAsync).ConfigureAwait(false);
                        await StepAsync(() => remoteLink).ConfigureAwait(false);

                        // The capture is let go before the next connection's sender makes its own: two at once are refused.
                        await StepAsync(previews.PauseAsync).ConfigureAwait(false);

                        // The send under way ends before the next connection's receiver can start one: staging is one folder.
                        await StepAsync(stopFiles.CancelAsync).ConfigureAwait(false);
                        await StepAsync(() => files).ConfigureAwait(false);

                        await StepAsync(stopLockLink.CancelAsync).ConfigureAwait(false);
                        await StepAsync(() => lockLink).ConfigureAwait(false);
                        await StepAsync(() => connection.DisposeAsync().AsTask()).ConfigureAwait(false);
                        log.Write($"The connection from console PC {consolePc} is closed.");
                    }
                },
                ct).ConfigureAwait(false);
        }
    }
}
