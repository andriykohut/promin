using Promin.Protocol.Transport;

namespace Promin.Protocol.Tests.Transport;

/// <summary>
/// Pairing against a machine in the middle that knows nothing and changes nothing.
/// </summary>
public class QuicPairingRelayTests
{
    private static readonly byte[] PresharedKey = [7, 7, 7, 7];

    /// <summary>
    /// A relay with certificates of its own sits between a real master and a real agent during a
    /// pairing window and forwards all four handshake messages unchanged. Both real machines hold
    /// the right pre-shared key. If the proofs covered only the nonces, both would verify — and
    /// each side would pin the relay's certificate, making the relay the trusted master for that
    /// agent and the trusted agent for that master without ever knowing the key.
    ///
    /// The proofs also cover both certificates, each side using its own and the one it actually
    /// received on its TLS connection. The two sides therefore compute proofs over different
    /// certificates, neither verifies, pairing fails on BOTH sides, and neither store changes.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task ARelayForwardingTheHandshakeUnchanged_IsNotPinnedByEitherSide()
    {
        using var deadline = QuicLoopbackHarness.Deadline();
        await using var harness = await QuicLoopbackHarness.StartAsync(
            deadline.Token, pinMasterOnAgent: false, pinAgentOnMaster: false);

        // The relay names its certificates after the machines it stands in for: nothing stops it.
        using var towardMaster = SelfSignedCertificateFactory.Create(QuicLoopbackHarness.AgentDeviceId);
        using var towardAgent = SelfSignedCertificateFactory.Create(QuicLoopbackHarness.MasterDeviceId);
        await using var relay = await PairingRelay.StartAsync(
            harness.Endpoint, towardMaster, towardAgent, QuicLoopbackHarness.PairingServerName, deadline.Token);

        var masterBefore = QuicLoopbackHarness.PersistedBytes(harness.MasterTrust);
        var agentBefore = QuicLoopbackHarness.PersistedBytes(harness.AgentTrust);

        var agentPairing = harness.Listener.AcceptPairingAsync(PresharedKey, deadline.Token);
        var master = new QuicMasterConnector(harness.MasterCertificate, harness.MasterTrust);
        var masterOutcome = await master.PairAsync(
            relay.Endpoint, QuicLoopbackHarness.AgentDeviceId, PresharedKey, deadline.Token);
        var agentOutcome = await agentPairing;

        // The relay really was in the path, in both directions: the agent answered the master's
        // challenge through it. This is not a test that fails because nothing got through.
        Assert.True(relay.BytesForwardedToAgent > 0, "the relay never forwarded the master's challenge");
        Assert.True(relay.BytesForwardedToMaster > 0, "the relay never forwarded the agent's answer");

        Assert.False(masterOutcome.Paired, "the master pinned the relay's certificate");
        Assert.False(agentOutcome.Paired, "the agent pinned the relay's certificate");
        Assert.Equal(masterBefore, QuicLoopbackHarness.PersistedBytes(harness.MasterTrust));
        Assert.Equal(agentBefore, QuicLoopbackHarness.PersistedBytes(harness.AgentTrust));
    }
}
