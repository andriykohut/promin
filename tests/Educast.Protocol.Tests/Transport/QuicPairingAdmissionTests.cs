using System.Net.Quic;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Educast.Protocol.Messages;
using Educast.Protocol.Pairing;
using Educast.Protocol.Transport;

namespace Educast.Protocol.Tests.Transport;

/// <summary>
/// Who an agent's pairing window lets in. A student PC that has no teacher PC pinned yet admits anyone; one
/// that has admits only that teacher PC, so that knowing the room code is of no use against a PC that has
/// already been paired.
/// </summary>
public class QuicPairingAdmissionTests
{
    private static readonly byte[] RoomCodeBytes = [7, 7, 7, 7];

    /// <summary>
    /// The agent has a teacher PC pinned and its window is open to pinned peers only. A stranger dials to pair:
    /// it is turned away during the TLS handshake, the window is not used up by it, and it never reaches a
    /// handshake, so it has had no guess at the code. The pinned teacher PC then pairs in that same window.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task PinnedPeersOnly_RefusesAStranger_WithoutSpendingAGuess()
    {
        using var deadline = QuicLoopbackHarness.Deadline();
        await using var harness = await QuicLoopbackHarness.StartAsync(deadline.Token);
        var agentBefore = QuicLoopbackHarness.PersistedBytes(harness.AgentTrust);

        var agentPairing = harness.Listener.AcceptPairingAsync(
            RoomCodeBytes, deadline.Token, admission: PairingAdmission.PinnedPeersOnly);

        using var stranger = SelfSignedCertificateFactory.Create("master-stranger");
        var rejectedBefore = harness.RejectedHandshakes;
        var refusal = await DialToPairAsync(harness, stranger, deadline.Token);

        Assert.True(
            refusal is AuthenticationException or QuicException or EndOfStreamException,
            $"expected the agent to refuse a stranger's pairing dial during TLS, got {refusal}");
        await WaitForRejectionAsync(harness, rejectedBefore, deadline.Token);

        // The refusal spent nothing: the operation is still waiting for a teacher PC.
        Assert.False(agentPairing.IsCompleted, "a stranger's dial used up the pairing window");

        var masterOutcome = await new QuicMasterConnector(harness.MasterCertificate, harness.MasterTrust)
            .PairAsync(harness.Endpoint, QuicLoopbackHarness.AgentDeviceId, RoomCodeBytes, deadline.Token);
        var agentOutcome = await agentPairing;

        Assert.True(masterOutcome.Paired, masterOutcome.FailureReason);
        Assert.True(agentOutcome.Paired, agentOutcome.FailureReason);
        Assert.Equal(agentBefore, QuicLoopbackHarness.PersistedBytes(harness.AgentTrust));
        Assert.False(harness.AgentTrust.TryGetPinnedThumbprint("master-stranger", out _));
    }

    [Fact(Timeout = 30000)]
    public async Task PinnedPeersOnly_LetsThePinnedMasterPairAgain()
    {
        using var deadline = QuicLoopbackHarness.Deadline();
        await using var harness = await QuicLoopbackHarness.StartAsync(deadline.Token);

        var agentPairing = harness.Listener.AcceptPairingAsync(
            RoomCodeBytes, deadline.Token, admission: PairingAdmission.PinnedPeersOnly);
        var masterOutcome = await new QuicMasterConnector(harness.MasterCertificate, harness.MasterTrust)
            .PairAsync(harness.Endpoint, QuicLoopbackHarness.AgentDeviceId, RoomCodeBytes, deadline.Token);
        var agentOutcome = await agentPairing;

        Assert.True(masterOutcome.Paired, masterOutcome.FailureReason);
        Assert.True(agentOutcome.Paired, agentOutcome.FailureReason);
        Assert.Equal(QuicLoopbackHarness.MasterDeviceId, agentOutcome.DeviceId);
    }

    /// <summary>
    /// The default is the old behaviour, which is what every caller before this parameter relied on: a master
    /// the agent has never seen is admitted. The control for the tests above.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task Anyone_IsTheDefault_AndAdmitsAMasterItHasNotPinned()
    {
        using var deadline = QuicLoopbackHarness.Deadline();
        await using var harness = await QuicLoopbackHarness.StartAsync(
            deadline.Token, pinMasterOnAgent: false, pinAgentOnMaster: false);

        var agentPairing = harness.Listener.AcceptPairingAsync(RoomCodeBytes, deadline.Token);
        var masterOutcome = await new QuicMasterConnector(harness.MasterCertificate, harness.MasterTrust)
            .PairAsync(harness.Endpoint, QuicLoopbackHarness.AgentDeviceId, RoomCodeBytes, deadline.Token);

        Assert.True(masterOutcome.Paired, masterOutcome.FailureReason);
        Assert.True((await agentPairing).Paired);
    }

    private static async Task<Exception?> DialToPairAsync(
        QuicLoopbackHarness harness, X509Certificate2 certificate, CancellationToken ct)
    {
        using var probeDeadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        probeDeadline.CancelAfter(TimeSpan.FromSeconds(5));

        return await Record.ExceptionAsync(async () =>
        {
            var connection = await harness.ConnectRawAsync(probeDeadline.Token, certificate, pairing: true);
            await using var stream = await connection.OpenOutboundStreamAsync(
                QuicStreamType.Bidirectional, probeDeadline.Token);
            await RawPairing.WriteAsync(
                stream, new PairChallengeRequest(PairingCrypto.CreateNonce()), probeDeadline.Token);
            await RawPairing.ReadAsync(stream, probeDeadline.Token);
        });
    }

    private static async Task WaitForRejectionAsync(QuicLoopbackHarness harness, int rejectedBefore, CancellationToken ct)
    {
        for (var poll = 0; poll < 100 && harness.RejectedHandshakes <= rejectedBefore; poll++)
        {
            await Task.Delay(50, ct);
        }

        Assert.True(harness.RejectedHandshakes > rejectedBefore, "the stranger was not refused during TLS");
    }
}
