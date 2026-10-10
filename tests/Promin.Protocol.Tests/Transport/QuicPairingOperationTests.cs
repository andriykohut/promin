using System.Diagnostics;
using System.Net;
using System.Net.Quic;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Promin.Protocol.Messages;
using Promin.Protocol.Pairing;
using Promin.Protocol.Transport;

namespace Promin.Protocol.Tests.Transport;

/// <summary>
/// First contact: the pairing operations, which are the only thing that creates a pin. Run over
/// real QUIC on loopback, starting from two machines that have never seen each other unless a
/// test says otherwise.
/// </summary>
public class QuicPairingOperationTests
{
    private static readonly byte[] PresharedKey = [7, 7, 7, 7];
    private static readonly byte[] WrongKey = [9, 9, 9, 9];
    private static readonly string UnrelatedThumbprint = new('C', 64);
    private static readonly string OtherThumbprint = new('D', 64);

    private static Task<QuicLoopbackHarness> StartUnpairedAsync(CancellationToken ct) =>
        QuicLoopbackHarness.StartAsync(ct, pinMasterOnAgent: false, pinAgentOnMaster: false);

    private static QuicMasterConnector MasterOf(QuicLoopbackHarness harness) =>
        new(harness.MasterCertificate, harness.MasterTrust);

    private static Task<PairingOutcome> PairAsync(QuicLoopbackHarness harness, CancellationToken ct) =>
        MasterOf(harness).PairAsync(harness.Endpoint, QuicLoopbackHarness.AgentDeviceId, PresharedKey, ct);

    // ---- The operation itself --------------------------------------------------------------

    /// <summary>
    /// The whole first-contact flow. Both operations pin what the peer actually presented, under
    /// the right device id, and the ordinary — strict — connection then succeeds on those pins.
    /// Nothing in the test pins anything by hand: fails an implementation where pinning is left
    /// to the caller.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task Pairing_PinsBothSides_AndTheOrdinaryConnectionThenSucceeds()
    {
        using var deadline = QuicLoopbackHarness.Deadline();
        await using var harness = await StartUnpairedAsync(deadline.Token);
        var agentThumbprint = PeerCertificateStore.ComputeThumbprint(harness.AgentCertificate);
        var masterThumbprint = PeerCertificateStore.ComputeThumbprint(harness.MasterCertificate);

        var agentPairing = harness.Listener.AcceptPairingAsync(PresharedKey, deadline.Token);
        var masterOutcome = await PairAsync(harness, deadline.Token);
        var agentOutcome = await agentPairing;

        Assert.True(masterOutcome.Paired, masterOutcome.FailureReason);
        Assert.True(agentOutcome.Paired, agentOutcome.FailureReason);
        Assert.Equal(PairingFailure.None, masterOutcome.Failure);
        Assert.Equal(QuicLoopbackHarness.AgentDeviceId, masterOutcome.DeviceId);
        Assert.Equal(QuicLoopbackHarness.MasterDeviceId, agentOutcome.DeviceId);
        Assert.Equal(agentThumbprint, masterOutcome.CertificateThumbprint);
        Assert.Equal(masterThumbprint, agentOutcome.CertificateThumbprint);

        Assert.True(harness.MasterTrust.TryGetPinnedThumbprint(QuicLoopbackHarness.AgentDeviceId, out var pinnedAgent));
        Assert.True(harness.AgentTrust.TryGetPinnedThumbprint(QuicLoopbackHarness.MasterDeviceId, out var pinnedMaster));
        Assert.Equal(agentThumbprint, pinnedAgent);
        Assert.Equal(masterThumbprint, pinnedMaster);

        var (master, agent) = await harness.ConnectAsync(deadline.Token);
        Assert.Equal(QuicLoopbackHarness.AgentDeviceId, master.PinnedDeviceId);
        Assert.Equal(QuicLoopbackHarness.MasterDeviceId, agent.PinnedDeviceId);
    }

    /// <summary>
    /// "Pin only on success" as a property of the code. A pairing that fails — here, the two
    /// machines were given different keys — must leave both stores byte-for-byte what they were,
    /// including the pins they already held for other devices.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task FailedPairing_LeavesBothStoresByteIdentical()
    {
        using var deadline = QuicLoopbackHarness.Deadline();
        await using var harness = await StartUnpairedAsync(deadline.Token);
        harness.MasterTrust.Pin("agent-other", UnrelatedThumbprint);
        harness.AgentTrust.Pin("master-other", OtherThumbprint);
        var masterBefore = QuicLoopbackHarness.PersistedBytes(harness.MasterTrust);
        var agentBefore = QuicLoopbackHarness.PersistedBytes(harness.AgentTrust);

        var agentPairing = harness.Listener.AcceptPairingAsync(WrongKey, deadline.Token);
        var masterOutcome = await PairAsync(harness, deadline.Token);
        var agentOutcome = await agentPairing;

        Assert.False(masterOutcome.Paired);
        Assert.False(agentOutcome.Paired);
        Assert.Equal(PairingFailure.KeyNotProven, masterOutcome.Failure);
        Assert.NotNull(masterOutcome.FailureReason);
        Assert.NotNull(agentOutcome.FailureReason);
        Assert.Null(masterOutcome.DeviceId);
        Assert.Null(agentOutcome.CertificateThumbprint);

        Assert.Equal(masterBefore, QuicLoopbackHarness.PersistedBytes(harness.MasterTrust));
        Assert.Equal(agentBefore, QuicLoopbackHarness.PersistedBytes(harness.AgentTrust));
    }

    /// <summary>
    /// The oracle, over the real transport. A machine the agent has never seen dials in to pair
    /// during a pairing window with a nonce of its own choosing, harvests the agent's answer, and
    /// then tries to be pinned without ever proving the pre-shared key — by claiming success with
    /// no proof, with a fabricated one, with the agent's own proof reflected back, or by saying
    /// nothing more. It must be REJECTED, and it must NOT be pinned: the agent's store is
    /// byte-identical afterwards, and the rogue is refused when it dials again.
    /// </summary>
    [Theory(Timeout = 30000)]
    [InlineData("none")]
    [InlineData("garbage")]
    [InlineData("reflected")]
    [InlineData("silent")]
    public async Task RogueMaster_ThatNeverProvesTheKey_IsRejected_AndIsNotPinned(string forgery)
    {
        using var deadline = QuicLoopbackHarness.Deadline();
        await using var harness = await StartUnpairedAsync(deadline.Token);
        harness.AgentTrust.Pin("master-other", OtherThumbprint);
        var before = QuicLoopbackHarness.PersistedBytes(harness.AgentTrust);

        using var rogueCertificate = SelfSignedCertificateFactory.Create("master-rogue");
        var rogueThumbprint = PeerCertificateStore.ComputeThumbprint(rogueCertificate);

        var agentPairing = harness.Listener.AcceptPairingAsync(
            PresharedKey, deadline.Token, handshakeTimeout: TimeSpan.FromSeconds(2));

        var rogue = await harness.ConnectRawAsync(deadline.Token, rogueCertificate, pairing: true);
        await using var stream = await rogue.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, deadline.Token);

        // The rogue does not hold the code: it runs J-PAKE with a guess.
        await RawPairing.MasterJPakeAsync(stream, WrongKey, deadline.Token);

        byte[] chosenNonce = [.. Enumerable.Repeat((byte)0xAB, PairingCrypto.NonceLength)];
        await RawPairing.WriteAsync(stream, new PairChallengeRequest(chosenNonce), deadline.Token);

        // The rogue does get an answer — that is inherent in answering a challenge at all. What
        // it must not get is trust.
        var harvested = Assert.IsType<PairChallengeResponse>(await RawPairing.ReadAsync(stream, deadline.Token));

        if (forgery != "silent")
        {
            byte[]? forgedProof = forgery switch
            {
                "none" => null,
                "garbage" => new byte[PairingCrypto.ProofLength],
                "reflected" => harvested.AgentProof,
                _ => throw new ArgumentOutOfRangeException(nameof(forgery)),
            };
            await RawPairing.WriteAsync(stream, new PairAck(Success: true, forgedProof), deadline.Token);
        }

        var outcome = await agentPairing;

        Assert.False(outcome.Paired);
        Assert.NotNull(outcome.FailureReason);
        Assert.Equal(before, QuicLoopbackHarness.PersistedBytes(harness.AgentTrust));
        Assert.False(harness.AgentTrust.TryGetPinnedThumbprint("master-rogue", out _));
        Assert.False(harness.AgentTrust.TryGetDeviceIdForThumbprint(rogueThumbprint, out _));

        // And it is a stranger again the moment the operation is over.
        await AssertRefusedAsync(harness, rogueCertificate, deadline.Token);
    }

    // ---- The window: explicit, single-use, bounded, and only for pairing connections --------

    /// <summary>
    /// The window belongs to one pairing operation, not to the listener. Once that operation is
    /// over — paired or not — a certificate the agent has no pin for is refused again, however
    /// it dials. Fails trust-on-first-use as a property of the listener object, where every
    /// stranger is accepted for as long as the listener lives.
    /// </summary>
    [Theory(Timeout = 30000)]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PairingWindow_IsGoneWhenItsOperationEnds_WhetherThatPairedOrNot(bool pairs)
    {
        using var deadline = QuicLoopbackHarness.Deadline();
        await using var harness = await StartUnpairedAsync(deadline.Token);

        var agentPairing = harness.Listener.AcceptPairingAsync(pairs ? PresharedKey : WrongKey, deadline.Token);
        var masterOutcome = await PairAsync(harness, deadline.Token);
        Assert.Equal(pairs, masterOutcome.Paired);
        Assert.Equal(pairs, (await agentPairing).Paired);

        using var stranger = SelfSignedCertificateFactory.Create("master-stranger");
        await AssertRefusedAsync(harness, stranger, deadline.Token);
    }

    /// <summary>
    /// Single-use: the window admits the first stranger to arrive and nobody after it. A second
    /// machine dialling to pair during the same window — while the first is still mid-handshake —
    /// is refused during its TLS handshake, and neither is pinned.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task PairingWindow_AdmitsOnlyTheFirstStranger()
    {
        using var deadline = QuicLoopbackHarness.Deadline();
        await using var harness = await StartUnpairedAsync(deadline.Token);
        var before = QuicLoopbackHarness.PersistedBytes(harness.AgentTrust);

        var agentPairing = harness.Listener.AcceptPairingAsync(
            PresharedKey, deadline.Token, handshakeTimeout: TimeSpan.FromSeconds(3));

        // The first stranger is admitted: it gets as far as an answer to its challenge.
        using var firstCertificate = SelfSignedCertificateFactory.Create("master-first");
        var first = await harness.ConnectRawAsync(deadline.Token, firstCertificate, pairing: true);
        await using var stream = await first.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, deadline.Token);
        await RawPairing.ChallengeAsync(stream, PresharedKey, deadline.Token);

        // The second is not, even though the window's operation is still running.
        using var secondCertificate = SelfSignedCertificateFactory.Create("master-second");
        await AssertRefusedAsync(harness, secondCertificate, deadline.Token);

        Assert.False((await agentPairing).Paired);
        Assert.Equal(before, QuicLoopbackHarness.PersistedBytes(harness.AgentTrust));
    }

    /// <summary>
    /// The window is bounded by its own deadline. The token passed here does not fire for
    /// another fifteen seconds; the operation must end by itself well before that, and leave no
    /// permission behind.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task PairingWindow_ClosesOnItsOwnDeadline_WithoutTheCallerCancelling()
    {
        using var deadline = QuicLoopbackHarness.Deadline();
        await using var harness = await StartUnpairedAsync(deadline.Token);

        var started = Stopwatch.StartNew();
        var outcome = await harness.Listener.AcceptPairingAsync(
            PresharedKey, deadline.Token, window: TimeSpan.FromMilliseconds(400));

        Assert.False(outcome.Paired);
        Assert.Equal(PairingFailure.NoMasterArrived, outcome.Failure);
        Assert.False(deadline.IsCancellationRequested);
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(8), $"the window stayed open for {started.Elapsed}");

        using var stranger = SelfSignedCertificateFactory.Create("master-late");
        await AssertRefusedAsync(harness, stranger, deadline.Token);
    }

    /// <summary>
    /// A caller may ask for a window long enough for a person to walk to the master and click
    /// Pair — and no longer. More than zero, at most ten minutes.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task PairingWindow_MustBeLongerThanZero_AndAtMostTenMinutes()
    {
        using var deadline = QuicLoopbackHarness.Deadline();
        await using var harness = await StartUnpairedAsync(deadline.Token);

        foreach (var refused in new[]
        {
            TimeSpan.Zero,
            TimeSpan.FromSeconds(-1),
            TimeSpan.FromMinutes(10) + TimeSpan.FromTicks(1),
            TimeSpan.FromHours(1),
        })
        {
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
                () => harness.Listener.AcceptPairingAsync(PresharedKey, deadline.Token, window: refused));
        }

        Assert.Equal(TimeSpan.FromMinutes(10), QuicTransportSettings.MaxPairingWindow);

        // Exactly ten minutes is accepted: it opens, and is still waiting when the caller gives up.
        using var soon = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        soon.CancelAfter(TimeSpan.FromMilliseconds(300));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => harness.Listener.AcceptPairingAsync(PresharedKey, soon.Token, window: TimeSpan.FromMinutes(10)));

        // A refused argument must not have left a window open behind it.
        Assert.Equal(
            PairingFailure.NoMasterArrived,
            (await harness.Listener.AcceptPairingAsync(
                PresharedKey, deadline.Token, window: TimeSpan.FromMilliseconds(200))).Failure);
    }

    [Fact(Timeout = 30000)]
    public async Task OnlyOnePairingWindow_CanBeOpenOnAListenerAtATime()
    {
        using var deadline = QuicLoopbackHarness.Deadline();
        await using var harness = await StartUnpairedAsync(deadline.Token);

        var first = harness.Listener.AcceptPairingAsync(
            PresharedKey, deadline.Token, window: TimeSpan.FromSeconds(1));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.Listener.AcceptPairingAsync(PresharedKey, deadline.Token));

        Assert.False((await first).Paired);

        // And once the first has ended, another may be opened.
        Assert.False((await harness.Listener.AcceptPairingAsync(
            PresharedKey, deadline.Token, window: TimeSpan.FromMilliseconds(200))).Paired);
    }

    /// <summary>
    /// An ordinary connection is judged by the pins and by nothing else, even while a pairing
    /// window is open: a stranger dialling normally is refused, does not use the window up, and
    /// the master that was meant to pair in that window still does. Fails a listener where an
    /// open window makes every inbound connection eligible for trust on first use.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task AnOrdinaryDial_IsNeverJudgedByAnOpenPairingWindow()
    {
        using var deadline = QuicLoopbackHarness.Deadline();
        await using var harness = await StartUnpairedAsync(deadline.Token);

        var agentPairing = harness.Listener.AcceptPairingAsync(PresharedKey, deadline.Token);

        using var stranger = SelfSignedCertificateFactory.Create("master-stranger");
        var rejectedBefore = harness.RejectedHandshakes;
        Assert.NotNull(await ProbeOrdinaryAsync(harness, stranger, deadline.Token));
        await WaitForRejectionAsync(harness, rejectedBefore, deadline.Token);
        Assert.False(agentPairing.IsCompleted, "an ordinary dial ended the pairing window");

        var masterOutcome = await PairAsync(harness, deadline.Token);

        Assert.True(masterOutcome.Paired, masterOutcome.FailureReason);
        Assert.True((await agentPairing).Paired);
    }

    /// <summary>
    /// A connection let in by a pairing window is not an ordinary connection and must never be
    /// handed out as one. An AcceptAsync that is waiting right through a successful pairing gets
    /// nothing from it. Fails a listener where pairing and ordinary accepts share one queue.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task AConnectionAdmittedForPairing_NeverComesOutOfAcceptAsync()
    {
        using var deadline = QuicLoopbackHarness.Deadline();
        await using var harness = await StartUnpairedAsync(deadline.Token);

        using var ordinaryWait = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        var ordinary = harness.Listener.AcceptAsync(ordinaryWait.Token);

        var agentPairing = harness.Listener.AcceptPairingAsync(PresharedKey, deadline.Token);
        var masterOutcome = await PairAsync(harness, deadline.Token);
        Assert.True(masterOutcome.Paired, masterOutcome.FailureReason);
        Assert.True((await agentPairing).Paired);

        await Task.Delay(500, deadline.Token);
        Assert.False(ordinary.IsCompleted, "the pairing connection was handed out as an ordinary one");

        await ordinaryWait.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ordinary);
    }

    /// <summary>
    /// A peer that has been let in to pair has proved nothing yet, and gets no video credit: it
    /// cannot open a single unidirectional stream at the agent, so it cannot make the agent
    /// buffer frames while the handshake runs. Fails a pairing connection that is given the
    /// agent's ordinary 256-stream credit, under which the stream opens at once.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task AConnectionAdmittedForPairing_IsGrantedNoVideoStreams()
    {
        using var deadline = QuicLoopbackHarness.Deadline();
        await using var harness = await StartUnpairedAsync(deadline.Token);

        var agentPairing = harness.Listener.AcceptPairingAsync(
            PresharedKey, deadline.Token, handshakeTimeout: TimeSpan.FromSeconds(4));

        using var unproven = SelfSignedCertificateFactory.Create("master-unproven");
        var connection = await harness.ConnectRawAsync(deadline.Token, unproven, pairing: true);
        await using var control = await connection.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, deadline.Token);

        // It is in: the agent answered its challenge.
        await RawPairing.ChallengeAsync(control, PresharedKey, deadline.Token);

        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        attempt.CancelAfter(TimeSpan.FromSeconds(1.5));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await connection.OpenOutboundStreamAsync(QuicStreamType.Unidirectional, attempt.Token));

        Assert.False((await agentPairing).Paired);
    }

    /// <summary>
    /// Pairing does not depend on the application calling AcceptAsync. Here nobody does: the
    /// ordinary accept queue is filled to the brim by a paired master's connections and then one
    /// more arrives. A different master then pairs — successfully. Fails an accept loop that
    /// waits for room in the ordinary queue, because the loop that is stuck waiting is the same
    /// loop that would have delivered the pairing connection.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task Pairing_Succeeds_WithAFullAcceptQueue_AndNobodyAccepting()
    {
        using var deadline = QuicLoopbackHarness.Deadline(40);
        await using var harness = await QuicLoopbackHarness.StartAsync(deadline.Token);
        var paired = MasterOf(harness);

        var unaccepted = new List<QuicChannel>();
        try
        {
            for (var i = 0; i < QuicAgentListener.AcceptQueueCapacity + 3; i++)
            {
                unaccepted.Add(await paired.ConnectAsync(
                    harness.Endpoint, QuicLoopbackHarness.AgentDeviceId, deadline.Token));
            }

            // The queue is full, and the surplus was turned away rather than waited on.
            for (var poll = 0;
                 poll < 100 && (harness.Listener.PendingAccepts < QuicAgentListener.AcceptQueueCapacity
                                || harness.RejectedHandshakes < 3);
                 poll++)
            {
                await Task.Delay(50, deadline.Token);
            }

            Assert.Equal(QuicAgentListener.AcceptQueueCapacity, harness.Listener.PendingAccepts);
            Assert.Equal(3, harness.RejectedHandshakes);

            using var newMasterCertificate = SelfSignedCertificateFactory.Create("master-2");
            var newMasterTrust = new PeerCertificateStore();

            var agentPairing = harness.Listener.AcceptPairingAsync(PresharedKey, deadline.Token);
            var masterOutcome = await new QuicMasterConnector(newMasterCertificate, newMasterTrust).PairAsync(
                harness.Endpoint, QuicLoopbackHarness.AgentDeviceId, PresharedKey, deadline.Token);
            var agentOutcome = await agentPairing;

            Assert.True(masterOutcome.Paired, masterOutcome.FailureReason);
            Assert.True(agentOutcome.Paired, agentOutcome.FailureReason);
            Assert.Equal("master-2", agentOutcome.DeviceId);
            Assert.Equal(QuicAgentListener.AcceptQueueCapacity, harness.Listener.PendingAccepts);
        }
        finally
        {
            foreach (var channel in unaccepted)
            {
                await channel.DisposeAsync();
            }
        }
    }

    // ---- Names and re-keying ------------------------------------------------------------------

    /// <summary>
    /// The master checks the name it dialled. An agent whose certificate names a different
    /// device than the one the operator asked to pair is refused before any proof is exchanged —
    /// the agent's pairing window is not even touched, so the right name still pairs in it.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task Master_RefusesAnAgentWhoseCertificateNamesAnotherDevice()
    {
        using var deadline = QuicLoopbackHarness.Deadline();
        await using var harness = await StartUnpairedAsync(deadline.Token);
        var before = QuicLoopbackHarness.PersistedBytes(harness.MasterTrust);

        var agentPairing = harness.Listener.AcceptPairingAsync(PresharedKey, deadline.Token);

        var wrongName = await MasterOf(harness).PairAsync(
            harness.Endpoint, "agent-2", PresharedKey, deadline.Token);

        Assert.False(wrongName.Paired);
        Assert.Equal(PairingFailure.PeerCertificateRefused, wrongName.Failure);
        Assert.Contains("'agent-1'", wrongName.FailureReason);
        Assert.Contains("'agent-2'", wrongName.FailureReason);
        Assert.Equal(before, QuicLoopbackHarness.PersistedBytes(harness.MasterTrust));
        Assert.False(harness.MasterTrust.TryGetPinnedThumbprint("agent-2", out _));
        Assert.False(agentPairing.IsCompleted, "the agent got as far as a handshake with a master that refused it");

        var rightName = await PairAsync(harness, deadline.Token);

        Assert.True(rightName.Paired, rightName.FailureReason);
        Assert.True((await agentPairing).Paired);
    }

    /// <summary>
    /// The connector binds the device id it is pairing. An agent id that is already pinned to a
    /// different certificate is not re-keyed by pairing it again — neither store changes — and
    /// becomes pairable only after an explicit unpin. This is the one failure whose reason may
    /// tell the operator to unpin, because it is the one where that is known to be the remedy.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task Master_DoesNotReKeyAPinnedAgent_UntilItIsExplicitlyUnpinned()
    {
        using var deadline = QuicLoopbackHarness.Deadline(40);
        await using var harness = await StartUnpairedAsync(deadline.Token);
        harness.MasterTrust.Pin(QuicLoopbackHarness.AgentDeviceId, UnrelatedThumbprint);
        var masterBefore = QuicLoopbackHarness.PersistedBytes(harness.MasterTrust);
        var agentBefore = QuicLoopbackHarness.PersistedBytes(harness.AgentTrust);

        var agentPairing = harness.Listener.AcceptPairingAsync(
            PresharedKey, deadline.Token, window: TimeSpan.FromSeconds(2));
        var refused = await PairAsync(harness, deadline.Token);

        Assert.False(refused.Paired);
        Assert.Equal(PairingFailure.PeerCertificateRefused, refused.Failure);
        Assert.Contains("unpin", refused.FailureReason, StringComparison.OrdinalIgnoreCase);
        Assert.False((await agentPairing).Paired);
        Assert.Equal(masterBefore, QuicLoopbackHarness.PersistedBytes(harness.MasterTrust));
        Assert.Equal(agentBefore, QuicLoopbackHarness.PersistedBytes(harness.AgentTrust));

        // The operator's explicit step, and then the same pairing goes through.
        Assert.True(harness.MasterTrust.Unpin(QuicLoopbackHarness.AgentDeviceId));

        agentPairing = harness.Listener.AcceptPairingAsync(PresharedKey, deadline.Token);
        var paired = await PairAsync(harness, deadline.Token);

        Assert.True(paired.Paired, paired.FailureReason);
        Assert.True((await agentPairing).Paired);
        Assert.True(harness.MasterTrust.TryGetPinnedThumbprint(QuicLoopbackHarness.AgentDeviceId, out var pinned));
        Assert.Equal(PeerCertificateStore.ComputeThumbprint(harness.AgentCertificate), pinned);
    }

    /// <summary>
    /// The same rule on the agent: a master name that is already pinned to a different
    /// certificate is not re-keyed by a machine turning up under that name, even one that knows
    /// the pre-shared key. And the agent finds out at once: the window was used up by a
    /// certificate it refused, so the operation returns promptly and says so — it does not sit
    /// out the rest of a thirty-second window and then report that nobody dialled.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task Agent_DoesNotReKeyAPinnedMasterName_AndSaysAtOnceThatItsWindowWasSpent()
    {
        using var deadline = QuicLoopbackHarness.Deadline(40);
        await using var harness = await StartUnpairedAsync(deadline.Token);
        harness.AgentTrust.Pin(QuicLoopbackHarness.MasterDeviceId, OtherThumbprint);
        var masterBefore = QuicLoopbackHarness.PersistedBytes(harness.MasterTrust);
        var agentBefore = QuicLoopbackHarness.PersistedBytes(harness.AgentTrust);

        var opened = Stopwatch.StartNew();
        var agentPairing = harness.Listener.AcceptPairingAsync(
            PresharedKey, deadline.Token, window: TimeSpan.FromSeconds(30));
        var masterOutcome = await PairAsync(harness, deadline.Token);
        var agentOutcome = await agentPairing;
        opened.Stop();

        Assert.False(masterOutcome.Paired);
        Assert.False(agentOutcome.Paired);
        Assert.Equal(PairingFailure.WindowSpent, agentOutcome.Failure);
        Assert.Contains(QuicLoopbackHarness.MasterDeviceId, agentOutcome.FailureReason);
        Assert.DoesNotContain("No master dialled", agentOutcome.FailureReason);
        Assert.True(
            opened.Elapsed < TimeSpan.FromSeconds(15),
            $"the agent waited {opened.Elapsed} of a 30 s window it could no longer use");

        // The master is told the truth too: the agent refused it. Not "unpin your agent".
        Assert.Equal(PairingFailure.RefusedByPeer, masterOutcome.Failure);
        Assert.DoesNotContain("unpin", masterOutcome.FailureReason, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(masterBefore, QuicLoopbackHarness.PersistedBytes(harness.MasterTrust));
        Assert.Equal(agentBefore, QuicLoopbackHarness.PersistedBytes(harness.AgentTrust));
    }

    // ---- What the master is told, and what it does not throw --------------------------------

    /// <summary>
    /// "The agent PC is not running" is the pairing failure an operator will meet most, and it
    /// must come back as an outcome with a reason of its own, not as an exception. The port here
    /// is one that was open a moment ago and is now closed, for which the runtime throws
    /// SocketException(HostUnreachable) — a type the previous version did not catch.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task PairAsync_ReturnsAnOutcome_WhenNothingIsListeningAtTheAddress()
    {
        using var deadline = QuicLoopbackHarness.Deadline();
        using var masterCertificate = SelfSignedCertificateFactory.Create("master-1");
        var trust = new PeerCertificateStore();

        int closedPort;
        using (var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
        {
            closedPort = ((IPEndPoint)udp.Client.LocalEndPoint!).Port;
        }

        var nowhere = new IPEndPoint(IPAddress.Loopback, closedPort);
        var outcome = await new QuicMasterConnector(masterCertificate, trust).PairAsync(
            nowhere, "agent-1", PresharedKey, deadline.Token);

        Assert.False(outcome.Paired);
        Assert.Equal(PairingFailure.AgentUnreachable, outcome.Failure);
        Assert.Contains(nowhere.ToString(), outcome.FailureReason);
        Assert.DoesNotContain("unpin", outcome.FailureReason, StringComparison.OrdinalIgnoreCase);
        Assert.False(trust.TryGetPinnedThumbprint("agent-1", out _));
    }

    /// <summary>
    /// A completed connection is not a completed pairing. The agent here has never paired with
    /// this master and has no pairing window open. TLS 1.3 still lets the master's ordinary
    /// connect succeed and hands it a channel — one that even reports the agent as pinned,
    /// because the master did authenticate the agent. Only using the connection reveals the
    /// refusal, and only the pairing operation's outcome says whether pairing happened. And that
    /// outcome tells the operator what is actually known: the agent refused, most likely because
    /// pairing is not open on it — not that a perfectly valid pin should be removed.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task ACompletedConnection_IsNotACompletedPairing()
    {
        using var deadline = QuicLoopbackHarness.Deadline();
        await using var harness = await QuicLoopbackHarness.StartAsync(deadline.Token, pinMasterOnAgent: false);
        var before = QuicLoopbackHarness.PersistedBytes(harness.MasterTrust);

        await using (var connected = await MasterOf(harness).ConnectAsync(
            harness.Endpoint, QuicLoopbackHarness.AgentDeviceId, deadline.Token))
        {
            Assert.Equal(QuicLoopbackHarness.AgentDeviceId, connected.PinnedDeviceId);

            var refusal = await Record.ExceptionAsync(async () =>
            {
                await using var control = await connected.OpenControlChannelAsync(deadline.Token);
                await control.SendAsync(new StopBroadcast(), deadline.Token);
                await control.ReceiveAsync(deadline.Token);
            });
            Assert.True(
                refusal is QuicException or AuthenticationException,
                $"expected the agent's refusal to surface on use, got {refusal}");
        }

        var outcome = await PairAsync(harness, deadline.Token);

        Assert.False(outcome.Paired);
        Assert.Equal(PairingFailure.RefusedByPeer, outcome.Failure);
        Assert.Contains("pairing", outcome.FailureReason, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("unpin", outcome.FailureReason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before, QuicLoopbackHarness.PersistedBytes(harness.MasterTrust));
        await harness.AssertNoConnectionReachesTheApplicationAsync(deadline.Token);
    }

    // ---- Success is explicit, and a half-finished pairing heals ------------------------------

    /// <summary>
    /// A timeout, a failure or a disconnection on the agent must never be readable by the master
    /// as acceptance. The agent here is a stand-in that does everything right — it holds the
    /// agent's real certificate and the right key, answers the challenge correctly and receives
    /// a valid proof from the master — and then, instead of confirming, does what an agent whose
    /// handshake deadline has just fired does. Whichever form that takes — closing the stream in
    /// good order, aborting it, or saying nothing at all — the master must end UNPINNED. Fails
    /// the previous rule, under which the stream closing gracefully meant "accepted".
    /// </summary>
    [Theory(Timeout = 30000)]
    [InlineData("closes the stream gracefully")]
    [InlineData("aborts the stream")]
    [InlineData("goes silent")]
    public async Task AnAgentThatGivesUpAfterTheMastersProof_LeavesTheMasterUnpinned(string givingUp)
    {
        using var deadline = QuicLoopbackHarness.Deadline();
        using var masterCertificate = SelfSignedCertificateFactory.Create("master-1");
        using var agentCertificate = SelfSignedCertificateFactory.Create("agent-1");
        var masterTrust = new PeerCertificateStore();
        var masterProofWasValid = false;

        await using var agent = await ScriptedAgent.StartAsync(
            agentCertificate,
            async (connection, stream, ct) =>
            {
                var sessionKey = await RawPairing.AgentJPakeAsync(stream, PresharedKey, ct);
                var challenge = Assert.IsType<PairChallengeRequest>(await RawPairing.ReadAsync(stream, ct));
                var binding = CertificateBinding.Compute(connection.RemoteCertificate!, agentCertificate);
                var agentNonce = PairingCrypto.CreateNonce();
                await RawPairing.WriteAsync(
                    stream,
                    new PairChallengeResponse(
                        PairingCrypto.ComputeAgentProof(sessionKey, challenge.MasterNonce, agentNonce, binding),
                        agentNonce),
                    ct);

                var ack = Assert.IsType<PairAck>(await RawPairing.ReadAsync(stream, ct));
                masterProofWasValid = PairingCrypto.VerifyMasterProof(
                    sessionKey, challenge.MasterNonce, agentNonce, binding, ack.MasterProof);

                // ...and here its deadline fires, before it has confirmed anything.
                switch (givingUp)
                {
                    case "closes the stream gracefully":
                        stream.CompleteWrites();
                        break;
                    case "aborts the stream":
                        stream.Abort(QuicAbortDirection.Both, QuicControlChannel.PairingFailedErrorCode);
                        break;
                }

                await ScriptedAgent.WaitForPeerToLeaveAsync(stream, ct);
            },
            deadline.Token);

        var outcome = await new QuicMasterConnector(masterCertificate, masterTrust).PairAsync(
            agent.Endpoint, "agent-1", PresharedKey, deadline.Token, timeout: TimeSpan.FromSeconds(3));
        await agent.Finished.WaitAsync(deadline.Token);

        // The handshake itself went all the way: this is not a test that fails early.
        Assert.True(masterProofWasValid, "the master never got as far as proving the key");

        Assert.False(outcome.Paired);
        Assert.Equal(PairingFailure.NotConfirmed, outcome.Failure);
        Assert.False(masterTrust.TryGetPinnedThumbprint("agent-1", out _));
        Assert.Equal(
            QuicLoopbackHarness.PersistedBytes(new PeerCertificateStore()),
            QuicLoopbackHarness.PersistedBytes(masterTrust));
    }

    /// <summary>
    /// The real agent, timing out. A master that holds the right certificate and the right key
    /// sends its challenge, takes the agent's answer, and then stalls past the agent's handshake
    /// deadline. The agent must abort the control stream — with the pairing-failed code, not a
    /// graceful close — must send no confirmation, and must have pinned nothing. Fails an agent
    /// whose own deadline skips the abort and lets the stream simply close.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task AnAgentSideHandshakeTimeout_AbortsTheStream_ConfirmsNothing_AndPinsNothing()
    {
        using var deadline = QuicLoopbackHarness.Deadline();
        await using var harness = await StartUnpairedAsync(deadline.Token);
        var before = QuicLoopbackHarness.PersistedBytes(harness.AgentTrust);

        var agentPairing = harness.Listener.AcceptPairingAsync(
            PresharedKey, deadline.Token, handshakeTimeout: TimeSpan.FromSeconds(2));

        var master = await harness.ConnectRawAsync(deadline.Token, pairing: true);
        await using var stream = await master.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, deadline.Token);
        await RawPairing.ChallengeAsync(stream, PresharedKey, deadline.Token);

        // The master now says nothing. What it reads next is whatever the agent does on timing out.
        var whatTheMasterSees = await Record.ExceptionAsync(() => RawPairing.ReadAsync(stream, deadline.Token));
        var agentOutcome = await agentPairing;

        var aborted = Assert.IsType<QuicException>(whatTheMasterSees);
        Assert.Equal(QuicError.StreamAborted, aborted.QuicError);
        Assert.Equal(QuicControlChannel.PairingFailedErrorCode, aborted.ApplicationErrorCode);

        Assert.False(agentOutcome.Paired);
        Assert.Equal(PairingFailure.TimedOut, agentOutcome.Failure);
        Assert.Equal(before, QuicLoopbackHarness.PersistedBytes(harness.AgentTrust));
    }

    /// <summary>
    /// The other direction of the same rule. The agent has verified the master, pinned it and
    /// confirmed — and the master then says, explicitly, that it did not record the pairing: it
    /// aborts the control stream with the pairing-failed code, which is what PairAsync does on
    /// every way out but success (its own deadline, its caller cancelling, a conflict on its own
    /// pins). The agent must not keep a pairing the master has disowned: it takes the pin back
    /// and reports that it did not pair. Fails an agent that treats its own confirmation as the
    /// end of the matter.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task AMasterThatDisownsThePairingAfterTheAgentConfirms_LeavesTheAgentUnpinned()
    {
        using var deadline = QuicLoopbackHarness.Deadline();
        await using var harness = await StartUnpairedAsync(deadline.Token);
        harness.AgentTrust.Pin("master-other", OtherThumbprint);
        var before = QuicLoopbackHarness.PersistedBytes(harness.AgentTrust);

        var agentPairing = harness.Listener.AcceptPairingAsync(PresharedKey, deadline.Token);
        var master = await harness.ConnectRawAsync(deadline.Token, pairing: true);
        await using var stream = await master.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, deadline.Token);

        var (sessionKey, masterNonce, answer) = await RawPairing.ChallengeAsync(stream, PresharedKey, deadline.Token);
        await RawPairing.WriteAsync(
            stream,
            new PairAck(
                Success: true,
                PairingCrypto.ComputeMasterProof(sessionKey, masterNonce, answer.AgentNonce, harness.Binding)),
            deadline.Token);
        Assert.IsType<PairConfirm>(await RawPairing.ReadAsync(stream, deadline.Token));

        stream.Abort(QuicAbortDirection.Both, QuicControlChannel.PairingFailedErrorCode);
        var outcome = await agentPairing;

        Assert.False(outcome.Paired);
        Assert.Equal(PairingFailure.NotConfirmed, outcome.Failure);
        Assert.False(harness.AgentTrust.TryGetPinnedThumbprint(QuicLoopbackHarness.MasterDeviceId, out _));
        Assert.Equal(before, QuicLoopbackHarness.PersistedBytes(harness.AgentTrust));
    }

    /// <summary>
    /// The state round 1 could not get out of: the agent has recorded the pairing and the master
    /// has not. It is produced here the way it happens — a master completes the handshake, the
    /// agent pins and confirms, and the master never records its half (its process died, or its
    /// caller cancelled). A plain retry, with no Unpin anywhere, then succeeds. Fails a listener
    /// that sends an already-pinned master's pairing dial to the ordinary accept queue, where
    /// the master's pairing attempt just times out.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task AgentOnlyPinned_HealsOnAPlainRetry_WithNoUnpin()
    {
        using var deadline = QuicLoopbackHarness.Deadline();
        await using var harness = await StartUnpairedAsync(deadline.Token);
        var masterThumbprint = PeerCertificateStore.ComputeThumbprint(harness.MasterCertificate);

        // A master that does everything right on the wire and then records nothing.
        var agentPairing = harness.Listener.AcceptPairingAsync(PresharedKey, deadline.Token);
        var forgetful = await harness.ConnectRawAsync(deadline.Token, pairing: true);
        await using (var stream = await forgetful.OpenOutboundStreamAsync(
                         QuicStreamType.Bidirectional, deadline.Token))
        {
            var (sessionKey, masterNonce, answer) =
                await RawPairing.ChallengeAsync(stream, PresharedKey, deadline.Token);
            Assert.True(PairingCrypto.VerifyAgentProof(
                sessionKey, masterNonce, answer.AgentNonce, harness.Binding, answer.AgentProof));
            await RawPairing.WriteAsync(
                stream,
                new PairAck(
                    Success: true,
                    PairingCrypto.ComputeMasterProof(sessionKey, masterNonce, answer.AgentNonce, harness.Binding)),
                deadline.Token);
            Assert.IsType<PairConfirm>(await RawPairing.ReadAsync(stream, deadline.Token));
        }

        Assert.True((await agentPairing).Paired);
        Assert.True(harness.AgentTrust.TryGetPinnedThumbprint(QuicLoopbackHarness.MasterDeviceId, out var pinned));
        Assert.Equal(masterThumbprint, pinned);
        Assert.False(harness.MasterTrust.TryGetPinnedThumbprint(QuicLoopbackHarness.AgentDeviceId, out _));
        var agentBefore = QuicLoopbackHarness.PersistedBytes(harness.AgentTrust);

        // The plain retry.
        agentPairing = harness.Listener.AcceptPairingAsync(PresharedKey, deadline.Token);
        var masterOutcome = await PairAsync(harness, deadline.Token);
        var agentOutcome = await agentPairing;

        Assert.True(masterOutcome.Paired, masterOutcome.FailureReason);
        Assert.True(agentOutcome.Paired, agentOutcome.FailureReason);
        Assert.True(harness.MasterTrust.TryGetPinnedThumbprint(QuicLoopbackHarness.AgentDeviceId, out _));
        Assert.Equal(agentBefore, QuicLoopbackHarness.PersistedBytes(harness.AgentTrust));

        var (master, agent) = await harness.ConnectAsync(deadline.Token);
        Assert.Equal(QuicLoopbackHarness.MasterDeviceId, agent.PinnedDeviceId);
        Assert.Equal(QuicLoopbackHarness.AgentDeviceId, master.PinnedDeviceId);
    }

    /// <summary>
    /// The mirror image, and the fully paired case: pairing the same two certificates again is
    /// idempotent whichever side — or both — already holds the pin. Nothing that was trusted
    /// changes; a pin that was missing is added.
    /// </summary>
    [Theory(Timeout = 30000)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task PairingTheSameCertificatesAgain_IsIdempotent(bool masterAlreadyPins, bool agentAlreadyPins)
    {
        using var deadline = QuicLoopbackHarness.Deadline();
        await using var harness = await QuicLoopbackHarness.StartAsync(
            deadline.Token, pinMasterOnAgent: agentAlreadyPins, pinAgentOnMaster: masterAlreadyPins);
        var masterBefore = QuicLoopbackHarness.PersistedBytes(harness.MasterTrust);

        var agentPairing = harness.Listener.AcceptPairingAsync(PresharedKey, deadline.Token);
        var masterOutcome = await PairAsync(harness, deadline.Token);
        var agentOutcome = await agentPairing;

        Assert.True(masterOutcome.Paired, masterOutcome.FailureReason);
        Assert.True(agentOutcome.Paired, agentOutcome.FailureReason);
        Assert.Equal(masterBefore, QuicLoopbackHarness.PersistedBytes(harness.MasterTrust));
        Assert.True(harness.AgentTrust.TryGetPinnedThumbprint(QuicLoopbackHarness.MasterDeviceId, out var pinned));
        Assert.Equal(PeerCertificateStore.ComputeThumbprint(harness.MasterCertificate), pinned);
    }

    // ---- Helpers -----------------------------------------------------------------------------

    /// <summary>
    /// Asserts the agent refuses a master it has no pin for, both ways it can dial — as an
    /// ordinary connection and as a pairing connection — and that each refusal happens during
    /// the TLS handshake itself, not merely by the connection being dropped afterwards.
    /// </summary>
    private static async Task AssertRefusedAsync(
        QuicLoopbackHarness harness, X509Certificate2 strangerCertificate, CancellationToken ct)
    {
        var rejectedBefore = harness.RejectedHandshakes;

        var ordinary = await ProbeOrdinaryAsync(harness, strangerCertificate, ct);
        Assert.True(
            ordinary is AuthenticationException or QuicException,
            $"expected the agent to refuse an ordinary dial from a master it has no pin for, got {ordinary}");

        var pairing = await ProbePairingAsync(harness, strangerCertificate, ct);
        Assert.True(
            pairing is AuthenticationException or QuicException or EndOfStreamException,
            $"expected the agent to refuse a pairing dial with no window open, got {pairing}");

        await harness.AssertNoConnectionReachesTheApplicationAsync(ct);
        Assert.True(
            harness.RejectedHandshakes >= rejectedBefore + 2,
            "a stranger's certificate was let through TLS; it was only dropped afterwards");
    }

    private static Task<Exception?> ProbeOrdinaryAsync(
        QuicLoopbackHarness harness, X509Certificate2 strangerCertificate, CancellationToken ct)
    {
        var strangerTrust = new PeerCertificateStore();
        strangerTrust.Pin(
            QuicLoopbackHarness.AgentDeviceId,
            PeerCertificateStore.ComputeThumbprint(harness.AgentCertificate));

        return QuicLoopbackHarness.ProbeConnectionAsync(
            new QuicMasterConnector(strangerCertificate, strangerTrust),
            harness.Endpoint, QuicLoopbackHarness.AgentDeviceId, ct);
    }

    /// <summary>Dials to pair, sends a challenge, and returns what stopped it getting an answer.</summary>
    private static async Task<Exception?> ProbePairingAsync(
        QuicLoopbackHarness harness, X509Certificate2 strangerCertificate, CancellationToken ct)
    {
        using var probeDeadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        probeDeadline.CancelAfter(TimeSpan.FromSeconds(5));

        return await Record.ExceptionAsync(async () =>
        {
            var connection = await harness.ConnectRawAsync(probeDeadline.Token, strangerCertificate, pairing: true);
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

        Assert.True(harness.RejectedHandshakes > rejectedBefore, "the ordinary dial was not refused during TLS");
    }
}
