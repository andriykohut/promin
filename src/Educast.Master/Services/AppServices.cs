using System.Diagnostics;
using System.Net;
using System.Net.Quic;
using Educast.Master.Core;
using Educast.Master.Core.Rooms;
using Educast.Master.Core.Setup;
using Educast.Master.Core.Wall;
using Educast.Protocol.Transport;
using Microsoft.UI.Dispatching;

namespace Educast.Master.Services;

/// <summary>
/// Everything the console owns for its whole life, built once in <c>App.OnLaunched</c>: the
/// paths under <c>%LocalAppData%\Educast\</c>, this PC's identity, the one QUIC connector, the
/// trust store, the room store, the two adapters the setup screen talks to, and the broadcast
/// controller and wall settings the room wall talks to.
/// </summary>
/// <remarks>
/// The paths are per Windows account because the identity is: its private key lives in the key
/// store of the account the console runs under, and the directory holds only public data.
/// </remarks>
public sealed class AppServices : IDisposable
{
    private readonly BroadcastController _broadcast;

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
        WallSettings = new JsonWallSettings(Path.Combine(rootDirectory, "settings.json"));
        Broadcast = _broadcast = new BroadcastController(Connector);
    }

    /// <summary>Where this Windows account's Educast state lives.</summary>
    public static string RootDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Educast");

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

    public static AppServices Create() => new(RootDirectory);

    /// <summary>
    /// Proves, off the UI thread, that this PC can make the secure connections Educast uses and
    /// that its certificate can complete a TLS handshake, so that a broken console says so
    /// instead of blaming every student PC it fails to reach. Loopback only: no port is opened
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
        // Not just Stop: a process that exits before the students were told leaves their screens on the last picture.
        _broadcast.StopAndWait(TimeSpan.FromSeconds(3));
        Discovery.Stop();
        Identity.Dispose();
    }
}
