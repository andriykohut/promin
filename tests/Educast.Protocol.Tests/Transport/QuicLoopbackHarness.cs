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

    /// <summary>The TLS server name a master uses when it dials in order to pair.</summary>
    public static string? PairingServerName => null;

    private readonly List<IAsyncDisposable> _ownedAsync = [];
    private readonly List<IDisposable> _owned = [];
    private readonly System.Runtime.CompilerServices.StrongBox<int> _rejectedHandshakes;

    private QuicLoopbackHarness(
        X509Certificate2 masterCertificate,
        X509Certificate2 agentCertificate,
        PeerCertificateStore masterTrust,
        PeerCertificateStore agentTrust,
        QuicAgentListener listener,
        System.Runtime.CompilerServices.StrongBox<int> rejectedHandshakes)
    {
        _rejectedHandshakes = rejectedHandshakes;
        MasterCertificate = masterCertificate;
        AgentCertificate = agentCertificate;
        MasterTrust = masterTrust;
        AgentTrust = agentTrust;
        Listener = listener;
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

    /// <summary>
    /// How many inbound handshakes the agent has refused — that is, peers turned away by TLS
    /// itself. A stranger that was let through TLS and only dropped afterwards does not count,
    /// which is exactly what lets a test tell the two apart.
    /// </summary>
    public int RejectedHandshakes => Volatile.Read(ref _rejectedHandshakes.Value);

    public IPEndPoint Endpoint => new(IPAddress.Loopback, Listener.LocalEndPoint.Port);

    /// <summary>
    /// Deadline applied to every await in these tests: a transport regression must fail loudly
    /// rather than hang a CI run forever.
    /// </summary>
    public static CancellationTokenSource Deadline(int seconds = 15) =>
        new(TimeSpan.FromSeconds(seconds));

    public static async Task<QuicLoopbackHarness> StartAsync(
        CancellationToken ct,
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
        var rejectedHandshakes = new System.Runtime.CompilerServices.StrongBox<int>();
        var listener = new QuicAgentListener(
            agentCertificate, agentTrust, port: 0, IPAddress.Loopback,
            rejected =>
            {
                Interlocked.Increment(ref rejectedHandshakes.Value);
                connectionRejected?.Invoke(rejected);
            });
        await listener.StartAsync(ct);

        return new QuicLoopbackHarness(
            masterCertificate, agentCertificate, masterTrust, agentTrust, listener, rejectedHandshakes);
    }

    /// <summary>Dials the agent and returns both ends of the established connection.</summary>
    public async Task<(QuicChannel Master, QuicChannel Agent)> ConnectAsync(
        CancellationToken ct,
        string deviceId = AgentDeviceId)
    {
        var acceptTask = Listener.AcceptAsync(ct);
        var connector = new QuicMasterConnector(MasterCertificate, MasterTrust);
        var master = await connector.ConnectAsync(Endpoint, deviceId, ct);
        var agent = await acceptTask;

        Own(master);
        Own(agent);
        return (master, agent);
    }

    /// <summary>
    /// Dials as the master but hands back the raw connection, so a test can write arbitrary
    /// bytes onto a control stream — the only way to reproduce a corrupt or fragmented frame
    /// from a peer that is hostile or simply broken. Presents the master's certificate unless
    /// another is given, which is how a test plays a machine the agent has never seen.
    /// </summary>
    public async Task<QuicConnection> ConnectRawAsync(
        CancellationToken ct, X509Certificate2? clientCertificate = null)
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
                ClientCertificates = [clientCertificate ?? MasterCertificate],
                RemoteCertificateValidationCallback = (_, _, _, _) => true,
            },
        };

        var connection = await QuicConnection.ConnectAsync(options, ct);
        Own(connection);
        return connection;
    }

    /// <summary>
    /// Opens the control stream from the master and starts the agent accepting it. The agent
    /// side is returned still pending on purpose: an outbound QUIC stream does not exist for
    /// the peer until its opener writes to it, so the accept only completes once the master
    /// sends its first control message. Awaiting both before the master speaks deadlocks, which
    /// is the same hazard that makes the master the side that speaks first in pairing.
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

    /// <summary>
    /// The binding both ends compute when they are talking to each other directly:
    /// SHA256(masterCertDER) || SHA256(agentCertDER).
    /// </summary>
    public byte[] Binding => CertificateBinding.Compute(MasterCertificate, AgentCertificate);

    /// <summary>The master's half of the handshake over an already-open control channel.</summary>
    public Task<bool> HandshakeAsMasterAsync(
        Educast.Protocol.Pairing.IControlChannel control,
        byte[] presharedKey,
        CancellationToken ct,
        TimeSpan? timeout = null) =>
        QuicPairingHandshake.RunAsMasterAsync(control, presharedKey, Binding, ct, timeout);

    /// <summary>The agent's half of the handshake over an already-open control channel.</summary>
    public Task<bool> HandshakeAsAgentAsync(
        Educast.Protocol.Pairing.IControlChannel control,
        byte[] presharedKey,
        CancellationToken ct,
        TimeSpan? timeout = null) =>
        QuicPairingHandshake.RunAsAgentAsync(control, presharedKey, Binding, ct, timeout);

    /// <summary>
    /// The exact bytes this store would persist — its whole trust state. Two snapshots that are
    /// equal mean nothing was pinned, unpinned or changed in between.
    /// </summary>
    public static byte[] PersistedBytes(PeerCertificateStore store)
    {
        var path = Path.Combine(Path.GetTempPath(), $"educast-snapshot-{Guid.NewGuid():N}.json");
        try
        {
            store.Save(path);
            return File.ReadAllBytes(path);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>Tests dial concurrently, so taking ownership has to be safe from any thread.</summary>
    private void Own(IAsyncDisposable disposable)
    {
        lock (_ownedAsync)
        {
            _ownedAsync.Add(disposable);
        }
    }

    public async ValueTask DisposeAsync()
    {
        IAsyncDisposable[] owned;
        lock (_ownedAsync)
        {
            owned = [.. _ownedAsync];
        }

        for (var i = owned.Length - 1; i >= 0; i--)
        {
            try
            {
                await owned[i].DisposeAsync();
            }
            catch (QuicException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
            catch (System.Security.Authentication.AuthenticationException)
            {
                // A raw connection the agent refused reports that when it is closed.
            }
        }

        foreach (var disposable in _owned)
        {
            disposable.Dispose();
        }
    }
}
