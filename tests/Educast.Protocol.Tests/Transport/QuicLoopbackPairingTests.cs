using System.Net.Quic;
using System.Security.Authentication;
using Educast.Protocol.Transport;

namespace Educast.Protocol.Tests.Transport;

/// <summary>
/// The handshake and certificate pinning over a real QUIC connection between two machines that
/// are already paired. Every test carries a bounded deadline so a regression that reintroduces a
/// handshake deadlock fails loudly instead of hanging the run. First contact — the pairing
/// operation that creates the pins — is in <see cref="QuicPairingOperationTests"/>.
/// </summary>
public class QuicLoopbackPairingTests
{
    private static readonly byte[] PresharedKey = [7, 7, 7, 7];

    /// <summary>
    /// The master opens the control stream, so the master must be the side that writes first:
    /// an outbound QUIC stream does not exist for the peer until its opener writes to it. Fails
    /// against the role assignment the brief originally carried (master as responder), which
    /// deadlocks for real over QUIC even though it passes over an in-memory channel.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task PairingSucceeds_OverRealLoopbackQuicConnection_WhenPresharedKeysMatch()
    {
        using var deadline = QuicLoopbackHarness.Deadline();
        await using var harness = await QuicLoopbackHarness.StartAsync(deadline.Token);
        var (master, agent) = await harness.ConnectAsync(deadline.Token);

        var (masterControl, agentPending) =
            await QuicLoopbackHarness.OpenControlPairAsync(master, agent, deadline.Token);
        await using var ownedMasterControl = masterControl;

        // The master writes first, which is what makes the stream exist for the agent at all.
        var masterTask = QuicPairingHandshake.RunAsMasterAsync(masterControl, PresharedKey, deadline.Token);
        await using var agentControl = await agentPending;
        var agentTask = QuicPairingHandshake.RunAsAgentAsync(agentControl, PresharedKey, deadline.Token);
        var results = await Task.WhenAll(masterTask, agentTask);

        Assert.All(results, succeeded => Assert.True(succeeded));
        Assert.Equal(QuicLoopbackHarness.AgentDeviceId, master.PinnedDeviceId);
        Assert.Equal(QuicLoopbackHarness.MasterDeviceId, agent.PinnedDeviceId);
    }

    /// <summary>Fails an implementation whose HMAC check always succeeds.</summary>
    [Fact(Timeout = 30000)]
    public async Task Pairing_FailsOnBothSides_WhenPresharedKeysDiffer()
    {
        using var deadline = QuicLoopbackHarness.Deadline();
        await using var harness = await QuicLoopbackHarness.StartAsync(deadline.Token);
        var (master, agent) = await harness.ConnectAsync(deadline.Token);

        var (masterControl, agentPending) =
            await QuicLoopbackHarness.OpenControlPairAsync(master, agent, deadline.Token);
        await using var ownedMasterControl = masterControl;

        var masterTask = QuicPairingHandshake.RunAsMasterAsync(masterControl, [1, 2, 3, 4], deadline.Token);
        await using var agentControl = await agentPending;
        var agentTask = QuicPairingHandshake.RunAsAgentAsync(agentControl, [9, 9, 9, 9], deadline.Token);
        var results = await Task.WhenAll(masterTask, agentTask);

        Assert.All(results, succeeded => Assert.False(succeeded));
    }

    /// <summary>
    /// A peer that connects and then says nothing must not be able to hang a pairing attempt.
    /// PairingService enforces no deadline of its own; the caller has to. Fails an
    /// implementation that passes CancellationToken.None, or none at all, into the handshake.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task MasterPairing_GivesUp_WhenTheAgentAcceptsTheConnectionAndThenSaysNothing()
    {
        using var deadline = QuicLoopbackHarness.Deadline();
        await using var harness = await QuicLoopbackHarness.StartAsync(deadline.Token);
        // The agent end is accepted but its control channel is deliberately never read.
        var (master, _) = await harness.ConnectAsync(deadline.Token);

        await using var masterControl = await master.OpenControlChannelAsync(deadline.Token);

        var started = DateTimeOffset.UtcNow;
        var paired = await QuicPairingHandshake.RunAsMasterAsync(
            masterControl, PresharedKey, deadline.Token, TimeSpan.FromSeconds(1));
        var elapsed = DateTimeOffset.UtcNow - started;

        Assert.False(paired);
        Assert.True(elapsed < TimeSpan.FromSeconds(10), $"handshake took {elapsed}, expected the 1s deadline");
    }

    /// <summary>
    /// Fails an implementation that accepts any certificate when it holds no pin — that is,
    /// one that leaves trust-on-first-use permanently switched on.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task Connection_IsRefused_WhenTheAgentHoldsNoPinForTheMaster()
    {
        using var deadline = QuicLoopbackHarness.Deadline();
        await using var harness = await QuicLoopbackHarness.StartAsync(deadline.Token, pinMasterOnAgent: false);

        var connector = new QuicMasterConnector(harness.MasterCertificate, harness.MasterTrust);
        var failure = await QuicLoopbackHarness.ProbeConnectionAsync(
            connector, harness.Endpoint, QuicLoopbackHarness.AgentDeviceId, deadline.Token);

        Assert.NotNull(failure);
        Assert.True(
            failure is AuthenticationException or QuicException,
            $"expected the agent to refuse an unpinned master, got {failure}");
        await harness.AssertNoConnectionReachesTheApplicationAsync(deadline.Token);
    }

    /// <summary>
    /// The agent pins a master under that master's real device id. Fails the implementation the
    /// brief specified, which pinned under the literal string "master" — there, any master
    /// presenting any certificate matches the single pin.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task Connection_IsRefused_WhenAnUnpinnedMasterDialsAnAgentThatAlreadyPinsAnother()
    {
        using var deadline = QuicLoopbackHarness.Deadline();
        await using var harness = await QuicLoopbackHarness.StartAsync(deadline.Token);

        // A second master machine with its own key, which this agent has never paired with.
        using var strangerCertificate = SelfSignedCertificateFactory.Create("master-2");
        var strangerTrust = new PeerCertificateStore();
        strangerTrust.Pin(
            QuicLoopbackHarness.AgentDeviceId,
            PeerCertificateStore.ComputeThumbprint(harness.AgentCertificate));

        var connector = new QuicMasterConnector(strangerCertificate, strangerTrust);
        var failure = await QuicLoopbackHarness.ProbeConnectionAsync(
            connector, harness.Endpoint, QuicLoopbackHarness.AgentDeviceId, deadline.Token);

        Assert.NotNull(failure);
        Assert.True(
            failure is AuthenticationException or QuicException,
            $"expected the agent to refuse a master it never pinned, got {failure}");
        await harness.AssertNoConnectionReachesTheApplicationAsync(deadline.Token);
    }

    /// <summary>
    /// The master pins each agent under its own device id too: a certificate pinned for one
    /// agent must not authenticate a different one. Fails an implementation that accepts any
    /// pinned thumbprint regardless of which device it was pinned for.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task Connection_IsRefused_WhenTheMasterDialsADeviceIdItHasNoPinFor()
    {
        using var deadline = QuicLoopbackHarness.Deadline();
        await using var harness = await QuicLoopbackHarness.StartAsync(deadline.Token);

        var connector = new QuicMasterConnector(harness.MasterCertificate, harness.MasterTrust);

        // The server certificate is validated during the master's own handshake, so this one
        // fails outright rather than after the fact.
        var failure = await Assert.ThrowsAnyAsync<Exception>(() =>
            connector.ConnectAsync(harness.Endpoint, "agent-2-never-paired", deadline.Token));
        Assert.True(
            failure is AuthenticationException or QuicException,
            $"expected the master to refuse an agent it has no pin for, got {failure}");
    }
}
