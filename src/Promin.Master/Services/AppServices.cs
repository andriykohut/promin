using System.Diagnostics;
using System.Net;
using System.Net.Quic;
using Promin.Master.Core;
using Promin.Master.Core.Rooms;
using Promin.Master.Core.Setup;
using Promin.Master.Core.Wall;
using Promin.Protocol.Transport;
using Microsoft.UI.Dispatching;

namespace Promin.Master.Services;

/// <summary>
/// Everything the console owns for its whole life, built once in <c>App.OnLaunched</c>: the
/// paths under <c>%LocalAppData%\Promin\</c>, this PC's identity, the one QUIC connector, the
/// trust store, the room store, the two adapters the setup screen talks to, and the room
/// connections, broadcast controller and wall settings the room wall talks to.
/// </summary>
/// <remarks>
/// The paths are per Windows account because the identity is: its private key lives in the key
/// store of the account the console runs under, and the directory holds only public data.
/// </remarks>
public sealed class AppServices : IDisposable
{
    private readonly BroadcastController _broadcast;
    private readonly RoomConnections<QuicPcConnection> _connections;

    private AppServices(string rootDirectory)
    {
        IdentityDirectory = Path.Combine(rootDirectory, "identity");
        TrustPath = Path.Combine(rootDirectory, "agents.json");
        RoomPath = Path.Combine(rootDirectory, "room.json");

        Identity = DeviceIdentity.LoadOrCreate(IdentityDirectory);
        TrustedAgents = new PeerCertificateStore();
        PairingClient.LoadTrust(TrustedAgents, TrustPath);
        Connector = new QuicMasterConnector(Identity.Certificate, TrustedAgents);

        RoomStore = new JsonRoomStore(RoomPath);
        Pairing = new QuicPairingClient(Connector, TrustedAgents, TrustPath);
        Discovery = new LanDiscovery(deviceId => TrustedAgents.TryGetPinnedThumbprint(deviceId, out _));
        _connections = new RoomConnections<QuicPcConnection>(DialAsync, () => DateTimeOffset.Now, (deviceId, failure) =>
            Trace.TraceInformation("Could not connect to {0}: {1} (tried again every few seconds while it is online; not logged again until it connects).", deviceId, failure.Message));
        Connections = _connections;
        WallSettings = new JsonWallSettings(SettingsPath);
        Broadcast = _broadcast = new BroadcastController(_connections);
    }

    /// <summary>Where this Windows account's Promin state lives.</summary>
    public static string RootDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Promin");

    /// <summary>The console's own choices: quality, sharpness, language.</summary>
    public static string SettingsPath => Path.Combine(RootDirectory, "settings.json");

    public string IdentityDirectory { get; }

    public string TrustPath { get; }

    public string RoomPath { get; }

    public DeviceIdentity Identity { get; }

    public PeerCertificateStore TrustedAgents { get; }

    public QuicMasterConnector Connector { get; }

    public IRoomStore RoomStore { get; }

    public IPairingClient Pairing { get; }

    public IDiscovery Discovery { get; }

    public ConsoleHealth Health { get; } = new();

    public IWallSettings WallSettings { get; }

    public IBroadcastController Broadcast { get; }

    /// <summary>Every online agent PC's standing connection: its screen pictures, and the broadcast.</summary>
    public IRoomConnections Connections { get; }

    public static AppServices Create() => new(RootDirectory);

    /// <summary>
    /// Proves, off the UI thread, that this PC can make the secure connections Promin uses and
    /// that its certificate can complete a TLS handshake, so that a broken console says so
    /// instead of blaming every agent PC it fails to reach. Loopback only: no port is opened
    /// to the network and no firewall prompt.
    /// </summary>
    /// <remarks>
    /// Two faults have words for the operator: no QUIC on this Windows, and an unusable
    /// certificate. Anything else the check throws is unexpected; it is written to the debug
    /// output (<see cref="Trace"/>) rather than swallowed, and no third message is invented.
    /// </remarks>
    public async Task CheckCertificateAsync(DispatcherQueue ui)
    {
        ArgumentNullException.ThrowIfNull(ui);

        LocalFault? fault = null;
        try
        {
            fault = await Task.Run(async () =>
            {
                // Asking msquic whether it is there can load it; not on the UI thread.
                if (!QuicListener.IsSupported || !QuicConnection.IsSupported)
                {
                    return LocalFault.NoSecureTransport;
                }

                await using var probe = new QuicAgentListener(
                    Identity.Certificate, new PeerCertificateStore(), port: 0, IPAddress.Loopback);
                await probe.StartAsync(CancellationToken.None).ConfigureAwait(false);
                return null;
            }).ConfigureAwait(false);
        }
        catch (PlatformNotSupportedException)
        {
            fault = LocalFault.NoSecureTransport;
        }
        catch (LocalCredentialException)
        {
            fault = LocalFault.UnusableCertificate;
        }
        catch (Exception e)
        {
            Trace.TraceError("The console's start-up certificate check failed unexpectedly: {0}", e);
        }

        if (fault is not null)
        {
            ui.TryEnqueue(() => Health.LocalFault = fault);
        }
    }

    public void Dispose()
    {
        // Not just Stop: a process that exits before the viewers were told leaves their screens on the last picture.
        _broadcast.StopAndWait(TimeSpan.FromSeconds(3));
        // Bounded (a second at most): closing tells each PC its connection is over.
        _connections.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2));
        Discovery.Stop();
        Identity.Dispose();
    }

    private static readonly TimeSpan DialTimeout = TimeSpan.FromSeconds(5);

    private async Task<QuicPcConnection> DialAsync(RoomMember pc, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(DialTimeout);
        var channel = await Connector
            .ConnectAsync(new IPEndPoint(IPAddress.Parse(pc.LastAddress), pc.Port), pc.DeviceId, deadline.Token)
            .ConfigureAwait(false);

        // The connector admits only the certificate pinned for this PC. A channel that does not say so (the pin was
        // removed while it connected) is not kept.
        if (channel.PinnedDeviceId != pc.DeviceId)
        {
            await channel.DisposeAsync().ConfigureAwait(false);
            throw new InvalidOperationException("The connection is not pinned to the PC that was dialled.");
        }

        return new QuicPcConnection(channel);
    }
}
