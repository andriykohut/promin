using System.Buffers;
using System.Net.Quic;
using System.Security.Authentication;
using Educast.Protocol.Messages;
using Educast.Protocol.Pairing;
using Educast.Protocol.Transport;

namespace Educast.Protocol.Tests.Transport;

/// <summary>
/// First contact: the pairing operations, which are the only thing that creates a pin. Run over
/// real QUIC on loopback, starting from two machines that have never seen each other.
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
        var masterOutcome = await MasterOf(harness).PairAsync(
            harness.Endpoint, QuicLoopbackHarness.AgentDeviceId, PresharedKey, deadline.Token);
        var agentOutcome = await agentPairing;

        Assert.True(masterOutcome.Paired, masterOutcome.FailureReason);
        Assert.True(agentOutcome.Paired, agentOutcome.FailureReason);
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
        var masterOutcome = await MasterOf(harness).PairAsync(
            harness.Endpoint, QuicLoopbackHarness.AgentDeviceId, PresharedKey, deadline.Token);
        var agentOutcome = await agentPairing;

        Assert.False(masterOutcome.Paired);
        Assert.False(agentOutcome.Paired);
        Assert.NotNull(masterOutcome.FailureReason);
        Assert.NotNull(agentOutcome.FailureReason);
        Assert.Null(masterOutcome.DeviceId);
        Assert.Null(agentOutcome.CertificateThumbprint);

        Assert.Equal(masterBefore, QuicLoopbackHarness.PersistedBytes(harness.MasterTrust));
        Assert.Equal(agentBefore, QuicLoopbackHarness.PersistedBytes(harness.AgentTrust));
    }

    /// <summary>
    /// The oracle, over the real transport. A machine the agent has never seen dials in during a
    /// pairing window with a nonce of its own choosing, harvests the agent's answer, and then
    /// tries to be pinned without ever proving the pre-shared key — by claiming success with no
    /// proof, with a fabricated one, with the agent's own proof reflected back, or by saying
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

        var rogue = await harness.ConnectRawAsync(deadline.Token, rogueCertificate);
        await using var stream = await rogue.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, deadline.Token);

        byte[] chosenNonce = [.. Enumerable.Repeat((byte)0xAB, PairingCrypto.NonceLength)];
        await stream.WriteAsync(ControlMessageCodec.Encode(new PairChallengeRequest(chosenNonce)), deadline.Token);

        // The rogue does get an answer — that is inherent in answering a challenge at all. What
        // it must not get is trust.
        var harvested = Assert.IsType<PairChallengeResponse>(await ReadMessageAsync(stream, deadline.Token));

        if (forgery != "silent")
        {
            byte[]? forgedProof = forgery switch
            {
                "none" => null,
                "garbage" => new byte[PairingCrypto.ProofLength],
                "reflected" => harvested.AgentProof,
                _ => throw new ArgumentOutOfRangeException(nameof(forgery)),
            };
            await stream.WriteAsync(
                ControlMessageCodec.Encode(new PairAck(Success: true, forgedProof)), deadline.Token);
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

    /// <summary>
    /// The window belongs to one pairing operation, not to the listener. Once that operation is
    /// over — paired or not — a certificate the agent has no pin for is refused again. Fails
    /// trust-on-first-use as a property of the listener object, where every stranger is accepted
    /// for as long as the listener lives.
    /// </summary>
    [Theory(Timeout = 30000)]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PairingWindow_IsGoneWhenItsOperationEnds_WhetherThatPairedOrNot(bool pairs)
    {
        using var deadline = QuicLoopbackHarness.Deadline();
        await using var harness = await StartUnpairedAsync(deadline.Token);

        var agentPairing = harness.Listener.AcceptPairingAsync(pairs ? PresharedKey : WrongKey, deadline.Token);
        var masterOutcome = await MasterOf(harness).PairAsync(
            harness.Endpoint, QuicLoopbackHarness.AgentDeviceId, PresharedKey, deadline.Token);
        Assert.Equal(pairs, masterOutcome.Paired);
        Assert.Equal(pairs, (await agentPairing).Paired);

        using var stranger = SelfSignedCertificateFactory.Create("master-stranger");
        await AssertRefusedAsync(harness, stranger, deadline.Token);
    }

    /// <summary>
    /// Single-use: the window admits the first stranger to arrive and nobody after it. A second
    /// machine dialling during the same window — while the first is still mid-handshake — is
    /// refused during its TLS handshake, and neither is pinned.
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
        var first = await harness.ConnectRawAsync(deadline.Token, firstCertificate);
        await using var stream = await first.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, deadline.Token);
        await stream.WriteAsync(
            ControlMessageCodec.Encode(new PairChallengeRequest(PairingCrypto.CreateNonce())), deadline.Token);
        Assert.IsType<PairChallengeResponse>(await ReadMessageAsync(stream, deadline.Token));

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

        var started = DateTimeOffset.UtcNow;
        var outcome = await harness.Listener.AcceptPairingAsync(
            PresharedKey, deadline.Token, window: TimeSpan.FromMilliseconds(400));
        var elapsed = DateTimeOffset.UtcNow - started;

        Assert.False(outcome.Paired);
        Assert.False(deadline.IsCancellationRequested);
        Assert.True(elapsed < TimeSpan.FromSeconds(8), $"the window stayed open for {elapsed}");

        using var stranger = SelfSignedCertificateFactory.Create("master-late");
        await AssertRefusedAsync(harness, stranger, deadline.Token);
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
        var masterOutcome = await MasterOf(harness).PairAsync(
            harness.Endpoint, QuicLoopbackHarness.AgentDeviceId, PresharedKey, deadline.Token);
        Assert.True(masterOutcome.Paired, masterOutcome.FailureReason);
        Assert.True((await agentPairing).Paired);

        await Task.Delay(500, deadline.Token);
        Assert.False(ordinary.IsCompleted, "the pairing connection was handed out as an ordinary one");

        await ordinaryWait.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ordinary);
    }

    /// <summary>
    /// The connector binds the device id it is pairing. An agent id that is already pinned to a
    /// different certificate is not re-keyed by pairing it again — neither store changes — and
    /// becomes pairable only after an explicit unpin.
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
        var refused = await MasterOf(harness).PairAsync(
            harness.Endpoint, QuicLoopbackHarness.AgentDeviceId, PresharedKey, deadline.Token);

        Assert.False(refused.Paired);
        Assert.Contains("unpin", refused.FailureReason, StringComparison.OrdinalIgnoreCase);
        Assert.False((await agentPairing).Paired);
        Assert.Equal(masterBefore, QuicLoopbackHarness.PersistedBytes(harness.MasterTrust));
        Assert.Equal(agentBefore, QuicLoopbackHarness.PersistedBytes(harness.AgentTrust));

        // The operator's explicit step, and then the same pairing goes through.
        Assert.True(harness.MasterTrust.Unpin(QuicLoopbackHarness.AgentDeviceId));

        agentPairing = harness.Listener.AcceptPairingAsync(PresharedKey, deadline.Token);
        var paired = await MasterOf(harness).PairAsync(
            harness.Endpoint, QuicLoopbackHarness.AgentDeviceId, PresharedKey, deadline.Token);

        Assert.True(paired.Paired, paired.FailureReason);
        Assert.True((await agentPairing).Paired);
        Assert.True(harness.MasterTrust.TryGetPinnedThumbprint(QuicLoopbackHarness.AgentDeviceId, out var pinned));
        Assert.Equal(PeerCertificateStore.ComputeThumbprint(harness.AgentCertificate), pinned);
    }

    /// <summary>
    /// The same rule on the agent: a master name that is already pinned to a different
    /// certificate is not re-keyed by a machine turning up under that name, even one that knows
    /// the pre-shared key.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task Agent_DoesNotReKeyAPinnedMasterName()
    {
        using var deadline = QuicLoopbackHarness.Deadline();
        await using var harness = await StartUnpairedAsync(deadline.Token);
        harness.AgentTrust.Pin(QuicLoopbackHarness.MasterDeviceId, OtherThumbprint);
        var masterBefore = QuicLoopbackHarness.PersistedBytes(harness.MasterTrust);
        var agentBefore = QuicLoopbackHarness.PersistedBytes(harness.AgentTrust);

        var agentPairing = harness.Listener.AcceptPairingAsync(
            PresharedKey, deadline.Token, window: TimeSpan.FromSeconds(2));
        var masterOutcome = await MasterOf(harness).PairAsync(
            harness.Endpoint, QuicLoopbackHarness.AgentDeviceId, PresharedKey, deadline.Token);

        Assert.False(masterOutcome.Paired);
        Assert.False((await agentPairing).Paired);
        Assert.Equal(masterBefore, QuicLoopbackHarness.PersistedBytes(harness.MasterTrust));
        Assert.Equal(agentBefore, QuicLoopbackHarness.PersistedBytes(harness.AgentTrust));
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
    /// A completed connection is not a completed pairing. The agent here has never paired with
    /// this master and has no pairing window open. TLS 1.3 still lets the master's connect
    /// succeed and hands it a channel — one that even reports the agent as pinned, because the
    /// master did authenticate the agent. Only using the connection reveals the refusal, and
    /// only the pairing operation's outcome says whether pairing happened. Fails code that
    /// treats "ConnectAsync returned" as "we are paired".
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

        var outcome = await MasterOf(harness).PairAsync(
            harness.Endpoint, QuicLoopbackHarness.AgentDeviceId, PresharedKey, deadline.Token);

        Assert.False(outcome.Paired);
        Assert.NotNull(outcome.FailureReason);
        Assert.Equal(before, QuicLoopbackHarness.PersistedBytes(harness.MasterTrust));
        await harness.AssertNoConnectionReachesTheApplicationAsync(deadline.Token);
    }

    /// <summary>
    /// Dials as an unknown master and asserts the agent refuses it during the TLS handshake
    /// itself — not merely that it is dropped afterwards — and surfaces nothing.
    /// </summary>
    private static async Task AssertRefusedAsync(
        QuicLoopbackHarness harness,
        System.Security.Cryptography.X509Certificates.X509Certificate2 strangerCertificate,
        CancellationToken ct)
    {
        var strangerTrust = new PeerCertificateStore();
        strangerTrust.Pin(
            QuicLoopbackHarness.AgentDeviceId,
            PeerCertificateStore.ComputeThumbprint(harness.AgentCertificate));
        var rejectedBefore = harness.RejectedHandshakes;

        var failure = await QuicLoopbackHarness.ProbeConnectionAsync(
            new QuicMasterConnector(strangerCertificate, strangerTrust),
            harness.Endpoint, QuicLoopbackHarness.AgentDeviceId, ct);

        Assert.NotNull(failure);
        Assert.True(
            failure is AuthenticationException or QuicException,
            $"expected the agent to refuse a master it has no pin for, got {failure}");
        await harness.AssertNoConnectionReachesTheApplicationAsync(ct);
        Assert.True(
            harness.RejectedHandshakes > rejectedBefore,
            "the stranger's certificate was let through TLS; it was only dropped afterwards");
    }

    /// <summary>Reads one whole control message off a raw stream.</summary>
    private static async Task<ControlMessage> ReadMessageAsync(QuicStream stream, CancellationToken ct)
    {
        var buffered = new ArrayBufferWriter<byte>();
        while (true)
        {
            var read = await stream.ReadAsync(buffered.GetMemory(4096), ct);
            Assert.True(read > 0, "the stream ended before a whole message arrived");
            buffered.Advance(read);

            if (ControlMessageCodec.TryDecode(
                    new ReadOnlySequence<byte>(buffered.WrittenMemory), out var message, out _))
            {
                return message!;
            }
        }
    }
}
