using System.Net;
using Educast.Master.Core;
using Educast.Master.Core.Rooms;
using Educast.Master.Core.Setup;
using Educast.Protocol.Transport;
using Microsoft.UI.Dispatching;

namespace Educast.Master.Services;

/// <summary>
/// Everything the console owns for its whole life, built once in <c>App.OnLaunched</c>: the
/// paths under <c>%LocalAppData%\Educast\</c>, this PC's identity, the one QUIC connector, the
/// trust store, the room store, and the two adapters the setup screen talks to.
/// </summary>
/// <remarks>
/// The paths are per Windows account because the identity is: its private key lives in the key
/// store of the account the console runs under, and the directory holds only public data.
/// </remarks>
public sealed class AppServices : IDisposable
{
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
        Discovery = new LanDiscovery();
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

    public static AppServices Create() => new(RootDirectory);

    /// <summary>
    /// Proves, off the UI thread, that this PC's certificate can complete a TLS handshake, so
    /// that a console with an unusable certificate says so instead of blaming every student PC
    /// it fails to reach. Loopback only: no port is opened to the network and no firewall prompt.
    /// </summary>
    public async Task CheckCertificateAsync(DispatcherQueue ui)
    {
        ArgumentNullException.ThrowIfNull(ui);

        try
        {
            await Task.Run(async () =>
            {
                await using var probe = new QuicAgentListener(
                    Identity.Certificate, new PeerCertificateStore(), port: 0, IPAddress.Loopback);
                await probe.StartAsync(CancellationToken.None).ConfigureAwait(false);
            }).ConfigureAwait(false);
        }
        catch (LocalCredentialException)
        {
            ui.TryEnqueue(() => Health.LocalFault = ConsoleHealth.CertificateFaultMessage);
        }
    }

    public void Dispose()
    {
        Discovery.Stop();
        Identity.Dispose();
    }
}
