using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Educast.Protocol.Messages;
using Educast.Protocol.Transport;

namespace Educast.Protocol.Tests.Transport;

/// <summary>
/// A master and an agent talking real QUIC over loopback. This is the only way to exercise
/// the transport without two machines: the TLS handshake, the pinning callbacks, stream
/// opening and stream credit are all genuine, only the network path is local.
/// </summary>
internal sealed class QuicLoopbackHarness : IAsyncDisposable
{
    public const string AgentDeviceId = "agent-1";
    public const string MasterDeviceId = "master-1";

    private readonly List<IAsyncDisposable> _ownedAsync = [];
    private readonly List<IDisposable> _owned = [];

    private QuicLoopbackHarness(
        X509Certificate2 masterCertificate,
        X509Certificate2 agentCertificate,
        PeerCertificateStore masterTrust,
        PeerCertificateStore agentTrust,
        QuicAgentListener listener,
        PeerTrustMode masterTrustMode)
    {
        MasterCertificate = masterCertificate;
        AgentCertificate = agentCertificate;
        MasterTrust = masterTrust;
        AgentTrust = agentTrust;
        Listener = listener;
        MasterTrustMode = masterTrustMode;
        _owned.Add(masterCertificate);
        _owned.Add(agentCertificate);
        _ownedAsync.Add(listener);
    }

    public X509Certificate2 MasterCertificate { get; }
    public X509Certificate2 AgentCertificate { get; }

    /// <summary>Pins the master holds for agents.</summary>
    public PeerCertificateStore MasterTrust { get; }

    /// <summary>Pins the agent holds for masters.</summary>
    public PeerCertificateStore AgentTrust { get; }

    public QuicAgentListener Listener { get; }
    public PeerTrustMode MasterTrustMode { get; }

    public IPEndPoint Endpoint => new(IPAddress.Loopback, Listener.LocalEndPoint.Port);

    /// <summary>
    /// Deadline applied to every await in these tests: a transport regression must fail loudly
    /// rather than hang a CI run forever.
    /// </summary>
    public static CancellationTokenSource Deadline(int seconds = 15) =>
        new(TimeSpan.FromSeconds(seconds));

    public static async Task<QuicLoopbackHarness> StartAsync(
        CancellationToken ct,
        PeerTrustMode masterTrustMode = PeerTrustMode.RequirePinned,
        PeerTrustMode agentTrustMode = PeerTrustMode.RequirePinned,
        bool pinMasterOnAgent = true,
        bool pinAgentOnMaster = true,
        Action<Exception>? connectionRejected = null)
    {
        var masterCertificate = SelfSignedCertificateFactory.Create(MasterDeviceId);
        var agentCertificate = SelfSignedCertificateFactory.Create(AgentDeviceId);

        var masterTrust = new PeerCertificateStore();
        if (pinAgentOnMaster)
        {
            masterTrust.Pin(AgentDeviceId, PeerCertificateStore.ComputeThumbprint(agentCertificate));
        }

        var agentTrust = new PeerCertificateStore();
        if (pinMasterOnAgent)
        {
            agentTrust.Pin(MasterDeviceId, PeerCertificateStore.ComputeThumbprint(masterCertificate));
        }

        // Port 0 so concurrent test runs never collide, loopback so no test run can trip a
        // Windows Firewall prompt. Production binds IPAddress.Any.
        var listener = new QuicAgentListener(
            agentCertificate, agentTrust, port: 0, agentTrustMode, IPAddress.Loopback, connectionRejected);
        await listener.StartAsync(ct);

        return new QuicLoopbackHarness(
            masterCertificate, agentCertificate, masterTrust, agentTrust, listener, masterTrustMode);
    }

    /// <summary>Dials the agent and returns both ends of the established connection.</summary>
    public async Task<(QuicChannel Master, QuicChannel Agent)> ConnectAsync(
        CancellationToken ct,
        string deviceId = AgentDeviceId)
    {
        var acceptTask = Listener.AcceptAsync(ct);
        var connector = new QuicMasterConnector(MasterCertificate, MasterTrust, MasterTrustMode);
        var master = await connector.ConnectAsync(Endpoint, deviceId, ct);
        var agent = await acceptTask;

        _ownedAsync.Add(master);
        _ownedAsync.Add(agent);
        return (master, agent);
    }

    /// <summary>
    /// Dials as the master but hands back the raw connection, so a test can write arbitrary
    /// bytes onto a control stream — the only way to reproduce a corrupt or fragmented frame
    /// from a peer that is hostile or simply broken.
    /// </summary>
    public async Task<QuicConnection> ConnectRawAsync(CancellationToken ct)
    {
        var options = new QuicClientConnectionOptions
        {
            RemoteEndPoint = Endpoint,
            DefaultStreamErrorCode = 0,
            DefaultCloseErrorCode = 0,
            IdleTimeout = QuicTransportSettings.IdleTimeout,
            KeepAliveInterval = QuicTransportSettings.KeepAliveInterval,
            MaxInboundBidirectionalStreams = QuicTransportSettings.MaxInboundBidirectionalStreams,
            MaxInboundUnidirectionalStreams = QuicTransportSettings.MasterMaxInboundUnidirectionalStreams,
            ClientAuthenticationOptions = new SslClientAuthenticationOptions
            {
                TargetHost = Endpoint.Address.ToString(),
                ApplicationProtocols = [QuicTransportSettings.ApplicationProtocol],
                ClientCertificates = [MasterCertificate],
                RemoteCertificateValidationCallback = (_, _, _, _) => true,
            },
        };

        var connection = await QuicConnection.ConnectAsync(options, ct);
        _ownedAsync.Add(connection);
        return connection;
    }

    /// <summary>
    /// Opens the control stream from the master and starts the agent accepting it. The agent
    /// side is returned still pending on purpose: an outbound QUIC stream does not exist for
    /// the peer until its opener writes to it, so the accept only completes once the master
    /// sends its first control message. Awaiting both before the master speaks deadlocks, which
    /// is the same hazard that fixes the master as the pairing verifier.
    /// </summary>
    public static async Task<(QuicControlChannel Master, Task<QuicControlChannel> AgentPending)> OpenControlPairAsync(
        QuicChannel master, QuicChannel agent, CancellationToken ct)
    {
        var masterControl = await master.OpenControlChannelAsync(ct);
        var agentPending = agent.AcceptControlChannelAsync(ct);
        return (masterControl, agentPending);
    }

    /// <summary>
    /// Drives a connection far enough to find out whether the agent actually accepted this
    /// master, and returns the failure if it did not. A TLS 1.3 client certificate is validated
    /// only after the client's own handshake has completed, so a master the agent refuses still
    /// gets a connection object back and only learns it was rejected when it tries to use it.
    /// </summary>
    public static async Task<Exception?> ProbeConnectionAsync(
        QuicMasterConnector connector, IPEndPoint endpoint, string deviceId, CancellationToken ct)
    {
        using var probeDeadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        probeDeadline.CancelAfter(TimeSpan.FromSeconds(5));

        return await Record.ExceptionAsync(async () =>
        {
            await using var channel = await connector.ConnectAsync(endpoint, deviceId, probeDeadline.Token);
            await using var control = await channel.OpenControlChannelAsync(probeDeadline.Token);
            await control.SendAsync(new StopBroadcast(), probeDeadline.Token);
            await control.ReceiveAsync(probeDeadline.Token);
        });
    }

    /// <summary>Asserts the listener never hands the application a connection it refused.</summary>
    public async Task AssertNoConnectionReachesTheApplicationAsync(CancellationToken ct)
    {
        using var brief = CancellationTokenSource.CreateLinkedTokenSource(ct);
        brief.CancelAfter(TimeSpan.FromSeconds(2));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Listener.AcceptAsync(brief.Token));
    }

    public static byte[] RandomBytes(int count) => RandomNumberGenerator.GetBytes(count);

    public async ValueTask DisposeAsync()
    {
        for (var i = _ownedAsync.Count - 1; i >= 0; i--)
        {
            try
            {
                await _ownedAsync[i].DisposeAsync();
            }
            catch (QuicException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        }

        foreach (var disposable in _owned)
        {
            disposable.Dispose();
        }
    }
}
