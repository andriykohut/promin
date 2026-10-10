using System.Security.Cryptography.X509Certificates;
using Promin.Protocol.Transport;

namespace Promin.Protocol.Tests.Transport;

/// <summary>
/// Certificates bound to a key in the Windows key store, as <see cref="DeviceIdentity"/> makes
/// them, used as the real credential on both sides of real QUIC over loopback: the agent's
/// listener (whose start-up self-handshake is the acid test of a credential) and the master's
/// connector, through first-contact pairing and then an ordinary strict connection.
/// </summary>
[Collection(KeyStoreCollection.Name)]
public sealed class DeviceIdentityTransportTests : IDisposable
{
    private readonly string _root = Path.Combine(
        IdentityTestCleanup.TempRoot("promin-identity-transport-tests"), Guid.NewGuid().ToString("N"));
    private readonly List<string> _keyNames = [];

    public DeviceIdentityTransportTests()
    {
        IdentityTestCleanup.SweepOnce();
    }

    public void Dispose()
    {
        // The ids are recorded the moment they are obtained, so a failing test still cleans up,
        // and one failed delete does not skip the other.
        try
        {
            IdentityTestCleanup.DeleteAll(_keyNames);
        }
        finally
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }

        var parent = Path.GetDirectoryName(_root)!;
        if (Directory.Exists(parent) && !Directory.EnumerateFileSystemEntries(parent).Any())
        {
            try
            {
                Directory.Delete(parent);
            }
            catch (IOException)
            {
                // Another test is using it at this moment.
            }
        }
        }
    }

    private X509Certificate2 NewIdentityCertificate(string name)
    {
        var identity = DeviceIdentity.LoadOrCreate(Path.Combine(_root, name));
        _keyNames.Add(IdentityTestCleanup.KeyNameFor(identity.DeviceId));
        return identity.Certificate;
    }

    [Fact(Timeout = 60000)]
    public async Task KeyStoreBackedCertificates_PairAndConnect_OverRealQuic_OnBothSides()
    {
        using var deadline = QuicLoopbackHarness.Deadline(30);
        X509Certificate2? agentCertificate = null;
        X509Certificate2? masterCertificate = null;

        // The harness owns, and disposes, the certificates it is given.
        await using var harness = await QuicLoopbackHarness.StartAsync(
            deadline.Token,
            pinMasterOnAgent: false,
            pinAgentOnMaster: false,
            createCertificate: subject => subject == QuicLoopbackHarness.AgentDeviceId
                ? agentCertificate = NewIdentityCertificate("agent")
                : masterCertificate = NewIdentityCertificate("master"));

        var agentId = CertificateIdentity.ReadDeviceId(agentCertificate!)!;
        var masterId = CertificateIdentity.ReadDeviceId(masterCertificate!)!;
        var presharedKey = new byte[] { 7, 7, 7, 7 };

        var agentPairing = harness.Listener.AcceptPairingAsync(presharedKey, deadline.Token);
        var masterOutcome = await new QuicMasterConnector(harness.MasterCertificate, harness.MasterTrust)
            .PairAsync(harness.Endpoint, agentId, presharedKey, deadline.Token);
        var agentOutcome = await agentPairing;

        Assert.True(masterOutcome.Paired, masterOutcome.FailureReason);
        Assert.True(agentOutcome.Paired, agentOutcome.FailureReason);
        Assert.Equal(agentId, masterOutcome.DeviceId);
        Assert.Equal(masterId, agentOutcome.DeviceId);

        var (master, agent) = await harness.ConnectAsync(deadline.Token, agentId);
        Assert.Equal(agentId, master.PinnedDeviceId);
        Assert.Equal(masterId, agent.PinnedDeviceId);
    }
}
