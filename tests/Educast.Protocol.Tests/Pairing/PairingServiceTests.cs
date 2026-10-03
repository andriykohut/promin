using Educast.Protocol.Messages;
using Educast.Protocol.Pairing;
using Educast.Protocol.Tests.TestDoubles;

namespace Educast.Protocol.Tests.Pairing;

/// <summary>
/// The mutual handshake, J-PAKE first:
/// <code>
/// master -> PairJPakeRound1        agent  -> PairJPakeRound1
/// master -> PairJPakeRound2        agent  -> PairJPakeRound2
///           both: sessionKey = J-PAKE output
/// master -> PairChallengeRequest(masterNonce)
/// agent  -> PairChallengeResponse(agentProof, agentNonce)
/// master -> PairAck(masterProof)
/// agent  -> verifies masterProof
/// </code>
/// Every proof is keyed by the session key, never by the room code. Roles are fixed by the
/// transport: the master opens the control stream, so the master speaks first and the agent waits
/// to receive. The tests' scripted peers speak the J-PAKE rounds themselves, through the same
/// <see cref="JPakeExchange"/> the service uses.
/// </summary>
public class PairingServiceTests
{
    private static readonly TimeSpan NoHang = TimeSpan.FromSeconds(5);

    /// <summary>The ASCII bytes of a room code, which is what the service takes.</summary>
    private static readonly byte[] Code = "482913"u8.ToArray();

    private static readonly byte[] WrongCode = "482914"u8.ToArray();

    /// <summary>
    /// What both sides pass when they are looking at the same two certificates. Opaque to the
    /// service; the transport supplies SHA256(masterCertDER) || SHA256(agentCertDER).
    /// </summary>
    private static readonly byte[] Binding = Enumerable.Range(0, 64).Select(i => (byte)(i + 1)).ToArray();

    [Fact]
    public async Task SameRoomCode_PairsBothSides()
    {
        var (masterChannel, agentChannel) = InMemoryControlChannel.CreatePair();
        var service = new PairingService();

        var masterTask = service.AuthenticateAsMasterAsync(masterChannel, Code, Binding, CancellationToken.None);
        var agentTask = service.AuthenticateAsAgentAsync(agentChannel, Code, Binding, CancellationToken.None);
        var results = await Task.WhenAll(masterTask, agentTask);

        Assert.All(results, succeeded => Assert.True(succeeded));
    }

    [Fact]
    public async Task WrongRoomCode_FailsBothSides_AndTheAgentIsToldSo()
    {
        var (masterChannel, agentChannel) = InMemoryControlChannel.CreatePair();
        var agentSide = new RecordingChannel(agentChannel);
        var service = new PairingService();

        var masterTask = service.AuthenticateAsMasterAsync(masterChannel, Code, Binding, CancellationToken.None);
        var agentTask = service.AuthenticateAsAgentAsync(agentSide, WrongCode, Binding, CancellationToken.None);

        Assert.False(await WithinDeadlineAsync(masterTask));
        Assert.False(await WithinDeadlineAsync(agentTask));

        var last = Assert.IsType<PairAck>(agentSide.Received[^1]);
        Assert.False(last.Success);
        Assert.Null(last.MasterProof);
    }

    /// <summary>
    /// The master's proof is what a stranger would need to test a guess against, so a master
    /// that has not been proven to the code must never send one.
    /// </summary>
    [Fact]
    public async Task Master_SendsNoProof_BeforeTheAgentProvesTheCode()
    {
        var (masterChannel, peerChannel) = InMemoryControlChannel.CreatePair();
        var masterSide = new RecordingChannel(masterChannel);

        var masterTask = new PairingService().AuthenticateAsMasterAsync(
            masterSide, Code, Binding, CancellationToken.None);

        // An agent that holds the wrong code and does everything else correctly.
        var wrongKey = await ScriptedAgentJPakeAsync(peerChannel, WrongCode);
        var challenge = Assert.IsType<PairChallengeRequest>(await ReceiveAsync(peerChannel));
        var agentNonce = PairingCrypto.CreateNonce();
        await peerChannel.SendAsync(
            new PairChallengeResponse(
                PairingCrypto.ComputeAgentProof(wrongKey, challenge.MasterNonce, agentNonce, Binding), agentNonce),
            CancellationToken.None);

        Assert.False(await WithinDeadlineAsync(masterTask));
        Assert.DoesNotContain(masterSide.Sent, message => message is PairAck { Success: true });
        Assert.DoesNotContain(masterSide.Sent, message => message is PairAck { MasterProof: not null });
    }

    /// <summary>
    /// A stranger who does not hold the code runs J-PAKE with a guess and gets the agent's
    /// answer. That answer must be no use for checking the guess offline: it is keyed by a session
    /// key the stranger did not derive, so recomputing it with the stranger's own key (or with the
    /// guess itself) gives something else.
    /// </summary>
    [Fact]
    public async Task StrangerWithoutTheCode_LearnsNothingToCheckAGuessAgainst()
    {
        var (strangerChannel, agentChannel) = InMemoryControlChannel.CreatePair();
        byte[] guess = "000000"u8.ToArray();

        var agentTask = new PairingService().AuthenticateAsAgentAsync(agentChannel, Code, Binding, CancellationToken.None);

        var strangerKey = await ScriptedMasterJPakeAsync(strangerChannel, guess);
        var masterNonce = PairingCrypto.CreateNonce();
        await strangerChannel.SendAsync(new PairChallengeRequest(masterNonce), CancellationToken.None);
        var recorded = Assert.IsType<PairChallengeResponse>(await ReceiveAsync(strangerChannel));

        Assert.False(PairingCrypto.ComputeAgentProof(strangerKey, masterNonce, recorded.AgentNonce, Binding)
            .SequenceEqual(recorded.AgentProof));
        Assert.False(PairingCrypto.ComputeAgentProof(guess, masterNonce, recorded.AgentNonce, Binding)
            .SequenceEqual(recorded.AgentProof));

        // And it cannot talk its way past the agent by claiming success.
        await strangerChannel.SendAsync(
            new PairAck(Success: true, new byte[PairingCrypto.ProofLength]), CancellationToken.None);
        Assert.False(await WithinDeadlineAsync(agentTask));
    }

    [Theory]
    [InlineData("master")]
    [InlineData("agent")]
    public async Task MalformedRound1_IsRejected_WithoutHanging(string underTest)
    {
        var (serviceChannel, peerChannel) = InMemoryControlChannel.CreatePair();
        var task = StartUnderTest(underTest, serviceChannel, Code);
        if (underTest == "master")
        {
            Assert.IsType<PairJPakeRound1>(await ReceiveAsync(peerChannel));
        }

        await peerChannel.SendAsync(new PairJPakeRound1([], [], [], []), CancellationToken.None);

        Assert.False(await WithinDeadlineAsync(task));
        AssertRejected(await ReceiveAsync(peerChannel));
    }

    [Theory]
    [InlineData("master")]
    [InlineData("agent")]
    public async Task MalformedRound2_IsRejected_WithoutHanging(string underTest)
    {
        var (serviceChannel, peerChannel) = InMemoryControlChannel.CreatePair();
        var task = StartUnderTest(underTest, serviceChannel, Code);

        // The peer plays round 1 genuinely, then breaks round 2.
        var peer = new JPakeExchange(underTest == "master" ? JPakeRole.Agent : JPakeRole.Master, Code);
        if (underTest == "master")
        {
            Assert.IsType<PairJPakeRound1>(await ReceiveAsync(peerChannel));
            await peerChannel.SendAsync(peer.CreateRound1(), CancellationToken.None);
            Assert.IsType<PairJPakeRound2>(await ReceiveAsync(peerChannel));
        }
        else
        {
            await peerChannel.SendAsync(peer.CreateRound1(), CancellationToken.None);
            Assert.IsType<PairJPakeRound1>(await ReceiveAsync(peerChannel));
        }

        await peerChannel.SendAsync(new PairJPakeRound2([], []), CancellationToken.None);

        Assert.False(await WithinDeadlineAsync(task));
        AssertRejected(await ReceiveAsync(peerChannel));
    }

    /// <summary>
    /// A peer still speaking the pre-J-PAKE protocol opens with the nonce challenge. That must be
    /// refused as the wrong message, not answered with a proof.
    /// </summary>
    [Theory]
    [InlineData("master")]
    [InlineData("agent")]
    public async Task WrongMessageWhereRound1IsExpected_IsRejected(string underTest)
    {
        var (serviceChannel, peerChannel) = InMemoryControlChannel.CreatePair();
        var task = StartUnderTest(underTest, serviceChannel, Code);
        if (underTest == "master")
        {
            Assert.IsType<PairJPakeRound1>(await ReceiveAsync(peerChannel));
        }

        await peerChannel.SendAsync(new PairChallengeRequest(PairingCrypto.CreateNonce()), CancellationToken.None);

        Assert.False(await WithinDeadlineAsync(task));
        AssertRejected(await ReceiveAsync(peerChannel));
    }

    /// <summary>
    /// The point of the exchange: proofs are keyed by what J-PAKE produced, so what crosses the
    /// wire says nothing about the code. A proof computed with the code itself does not match, and
    /// one computed with the session key does.
    /// </summary>
    [Fact]
    public async Task ProofsAreKeyedByTheSessionKey_NotTheCode()
    {
        var (masterChannel, agentChannel) = InMemoryControlChannel.CreatePair();

        var agentTask = new PairingService().AuthenticateAsAgentAsync(agentChannel, Code, Binding, CancellationToken.None);

        var sessionKey = await ScriptedMasterJPakeAsync(masterChannel, Code);
        var masterNonce = PairingCrypto.CreateNonce();
        await masterChannel.SendAsync(new PairChallengeRequest(masterNonce), CancellationToken.None);
        var captured = Assert.IsType<PairChallengeResponse>(await ReceiveAsync(masterChannel));

        Assert.False(PairingCrypto.ComputeAgentProof(Code, masterNonce, captured.AgentNonce, Binding)
            .SequenceEqual(captured.AgentProof));
        Assert.True(PairingCrypto.VerifyAgentProof(sessionKey, masterNonce, captured.AgentNonce, Binding, captured.AgentProof));

        await masterChannel.SendAsync(PairAck.Rejected, CancellationToken.None);
        Assert.False(await WithinDeadlineAsync(agentTask));
    }

    [Fact]
    public async Task AuthenticateBothSides_Fail_WhenRoomCodesDiffer()
    {
        var (masterChannel, agentChannel) = InMemoryControlChannel.CreatePair();
        var service = new PairingService();

        var masterTask = service.AuthenticateAsMasterAsync(masterChannel, Code, Binding, CancellationToken.None);
        var agentTask = service.AuthenticateAsAgentAsync(agentChannel, WrongCode, Binding, CancellationToken.None);
        var results = await Task.WhenAll(masterTask, agentTask);

        Assert.All(results, succeeded => Assert.False(succeeded));
    }

    /// <summary>
    /// The relay, at the protocol level. Both sides hold the right code, and every message is
    /// delivered unchanged — but each side was handed a different binding, which is what happens
    /// when each is looking at a middle party's certificate instead of the other's. J-PAKE alone
    /// succeeds here; neither proof verifies, so neither side pairs. Fails proofs that cover only
    /// the nonces, or a session key that is not bound to the certificates.
    /// </summary>
    [Fact]
    public async Task AuthenticateBothSides_Fail_WhenTheyAreNotLookingAtTheSameCertificates()
    {
        var (masterChannel, agentChannel) = InMemoryControlChannel.CreatePair();
        var service = new PairingService();
        var seenByAgent = (byte[])Binding.Clone();
        seenByAgent[0] ^= 0xFF;

        var masterTask = service.AuthenticateAsMasterAsync(masterChannel, Code, Binding, CancellationToken.None);
        var agentTask = service.AuthenticateAsAgentAsync(agentChannel, Code, seenByAgent, CancellationToken.None);

        Assert.False(await WithinDeadlineAsync(masterTask));
        Assert.False(await WithinDeadlineAsync(agentTask));
    }

    /// <summary>
    /// An empty binding would turn the relay protection off without anything looking wrong, so
    /// it is refused as a programming error on both sides rather than accepted as a mode.
    /// </summary>
    [Fact]
    public async Task Authenticate_RefusesToRun_WithoutABinding()
    {
        var (masterChannel, agentChannel) = InMemoryControlChannel.CreatePair();
        var service = new PairingService();

        await Assert.ThrowsAsync<ArgumentException>(() => service.AuthenticateAsMasterAsync(
            masterChannel, Code, ReadOnlyMemory<byte>.Empty, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => service.AuthenticateAsAgentAsync(
            agentChannel, Code, ReadOnlyMemory<byte>.Empty, CancellationToken.None));
    }

    /// <summary>
    /// The oracle, made concrete. A rogue master gets through J-PAKE, sends a nonce of its
    /// choosing, harvests the agent's answer, and claims success without ever proving the session
    /// key. The agent must refuse — this is the test the one-directional handshake fails, and its
    /// <c>false</c> is what keeps the rogue's certificate from being pinned.
    /// </summary>
    [Theory]
    [InlineData("none")]
    [InlineData("garbage")]
    [InlineData("reflected")]
    public async Task AuthenticateAsAgent_Fails_WhenTheMasterNeverProvesTheKey(string forgery)
    {
        var (roguePeer, agentChannel) = InMemoryControlChannel.CreatePair();

        var agentTask = new PairingService().AuthenticateAsAgentAsync(agentChannel, Code, Binding, CancellationToken.None);

        await ScriptedMasterJPakeAsync(roguePeer, Code);

        // A nonce the rogue chose, not one it was given.
        byte[] chosenNonce = [.. Enumerable.Repeat((byte)0xAB, PairingCrypto.NonceLength)];
        await roguePeer.SendAsync(new PairChallengeRequest(chosenNonce), CancellationToken.None);

        var reply = Assert.IsType<PairChallengeResponse>(await ReceiveAsync(roguePeer));

        byte[]? forgedProof = forgery switch
        {
            "none" => null,
            "garbage" => new byte[PairingCrypto.ProofLength],
            // The reflection attempt: hand the agent's own proof back as the master's.
            "reflected" => reply.AgentProof,
            _ => throw new ArgumentOutOfRangeException(nameof(forgery)),
        };
        await roguePeer.SendAsync(new PairAck(Success: true, forgedProof), CancellationToken.None);

        Assert.False(await WithinDeadlineAsync(agentTask));
    }

    /// <summary>
    /// A rogue that does know the agent's proof still cannot pair: the master's proof is a
    /// different value under a different label, and only the session key produces it.
    /// </summary>
    [Fact]
    public async Task AuthenticateAsAgent_Succeeds_OnlyForAProofComputedWithTheSessionKey()
    {
        var (masterChannel, agentChannel) = InMemoryControlChannel.CreatePair();

        var agentTask = new PairingService().AuthenticateAsAgentAsync(agentChannel, Code, Binding, CancellationToken.None);

        var sessionKey = await ScriptedMasterJPakeAsync(masterChannel, Code);
        byte[] masterNonce = PairingCrypto.CreateNonce();
        await masterChannel.SendAsync(new PairChallengeRequest(masterNonce), CancellationToken.None);
        var reply = Assert.IsType<PairChallengeResponse>(await ReceiveAsync(masterChannel));

        // The agent's proof must itself be the specified value over both nonces.
        Assert.True(PairingCrypto.VerifyAgentProof(sessionKey, masterNonce, reply.AgentNonce, Binding, reply.AgentProof));

        await masterChannel.SendAsync(
            new PairAck(Success: true, PairingCrypto.ComputeMasterProof(sessionKey, masterNonce, reply.AgentNonce, Binding)),
            CancellationToken.None);

        Assert.True(await WithinDeadlineAsync(agentTask));
    }

    /// <summary>
    /// The same ack, with the master proof computed from the room code instead of the session
    /// key, is refused: knowing the code is not enough, only the J-PAKE output pairs.
    /// </summary>
    [Fact]
    public async Task AuthenticateAsAgent_Fails_ForAMasterProofKeyedByTheCode()
    {
        var (masterChannel, agentChannel) = InMemoryControlChannel.CreatePair();

        var agentTask = new PairingService().AuthenticateAsAgentAsync(agentChannel, Code, Binding, CancellationToken.None);

        await ScriptedMasterJPakeAsync(masterChannel, Code);
        byte[] masterNonce = PairingCrypto.CreateNonce();
        await masterChannel.SendAsync(new PairChallengeRequest(masterNonce), CancellationToken.None);
        var reply = Assert.IsType<PairChallengeResponse>(await ReceiveAsync(masterChannel));

        await masterChannel.SendAsync(
            new PairAck(Success: true, PairingCrypto.ComputeMasterProof(Code, masterNonce, reply.AgentNonce, Binding)),
            CancellationToken.None);

        Assert.False(await WithinDeadlineAsync(agentTask));
    }

    /// <summary>
    /// A malformed nonce is peer-controlled input: a clean false, never an exception, and never a
    /// proof computed over it.
    /// </summary>
    [Fact]
    public async Task AuthenticateAsAgent_Fails_WhenTheMasterNonceIsTheWrongLength()
    {
        var (peerChannel, agentChannel) = InMemoryControlChannel.CreatePair();

        var agentTask = new PairingService().AuthenticateAsAgentAsync(agentChannel, Code, Binding, CancellationToken.None);
        await ScriptedMasterJPakeAsync(peerChannel, Code);
        await peerChannel.SendAsync(new PairChallengeRequest([1, 2, 3]), CancellationToken.None);

        Assert.False(await WithinDeadlineAsync(agentTask));
        Assert.False(Assert.IsType<PairAck>(await ReceiveAsync(peerChannel)).Success);
    }

    [Fact]
    public async Task AuthenticateAsMaster_Fails_WhenTheAgentNonceIsTheWrongLength()
    {
        var (masterChannel, peerChannel) = InMemoryControlChannel.CreatePair();

        var masterTask = new PairingService().AuthenticateAsMasterAsync(
            masterChannel, Code, Binding, CancellationToken.None);

        var key = await ScriptedAgentJPakeAsync(peerChannel, Code);
        var challenge = Assert.IsType<PairChallengeRequest>(await ReceiveAsync(peerChannel));
        byte[] shortNonce = [1, 2, 3];
        await peerChannel.SendAsync(
            new PairChallengeResponse(PairingCrypto.ComputeAgentProof(key, challenge.MasterNonce, shortNonce, Binding), shortNonce),
            CancellationToken.None);

        Assert.False(await WithinDeadlineAsync(masterTask));
        Assert.False(Assert.IsType<PairAck>(await ReceiveAsync(peerChannel)).Success);
    }

    /// <summary>
    /// The agent must SIGNAL a peer it is giving up on. Returning silently leaves the master
    /// blocked on a receive until its deadline expires, which is the Task 3 review requirement
    /// this pins. Here the J-PAKE rounds are fine and the challenge position holds another message.
    /// </summary>
    [Fact]
    public async Task AuthenticateAsAgent_SignalsThePeer_WhenTheFirstMessageIsNotAChallenge()
    {
        var (peerChannel, agentChannel) = InMemoryControlChannel.CreatePair();

        var agentTask = new PairingService().AuthenticateAsAgentAsync(agentChannel, Code, Binding, CancellationToken.None);
        await ScriptedMasterJPakeAsync(peerChannel, Code);
        await peerChannel.SendAsync(new StopBroadcast(), CancellationToken.None);

        Assert.False(await WithinDeadlineAsync(agentTask));

        // The peer is told, rather than left waiting.
        var signal = Assert.IsType<PairAck>(await ReceiveAsync(peerChannel));
        Assert.False(signal.Success);
        Assert.Null(signal.MasterProof);
    }

    /// <summary>
    /// The mirror of the above: a master that gives up tells the agent, so the agent is not left
    /// blocked waiting for an acknowledgement that will never come.
    /// </summary>
    [Fact]
    public async Task AuthenticateAsMaster_FailsAndSignals_WhenTheReplyIsNotAChallengeResponse()
    {
        var (masterChannel, peerChannel) = InMemoryControlChannel.CreatePair();

        var masterTask = new PairingService().AuthenticateAsMasterAsync(
            masterChannel, Code, Binding, CancellationToken.None);

        await ScriptedAgentJPakeAsync(peerChannel, Code);
        Assert.IsType<PairChallengeRequest>(await ReceiveAsync(peerChannel));
        await peerChannel.SendAsync(new BroadcastReady(), CancellationToken.None);

        Assert.False(await WithinDeadlineAsync(masterTask));

        var signal = Assert.IsType<PairAck>(await ReceiveAsync(peerChannel));
        Assert.False(signal.Success);
        Assert.Null(signal.MasterProof);
    }

    /// <summary>A forged agent proof is refused, and the agent is told so.</summary>
    [Fact]
    public async Task AuthenticateAsMaster_Fails_WhenTheAgentProofIsForged()
    {
        var (masterChannel, peerChannel) = InMemoryControlChannel.CreatePair();

        var masterTask = new PairingService().AuthenticateAsMasterAsync(
            masterChannel, Code, Binding, CancellationToken.None);

        await ScriptedAgentJPakeAsync(peerChannel, Code);
        Assert.IsType<PairChallengeRequest>(await ReceiveAsync(peerChannel));
        await peerChannel.SendAsync(
            new PairChallengeResponse(new byte[PairingCrypto.ProofLength], PairingCrypto.CreateNonce()),
            CancellationToken.None);

        Assert.False(await WithinDeadlineAsync(masterTask));
        Assert.False(Assert.IsType<PairAck>(await ReceiveAsync(peerChannel)).Success);
    }

    [Fact]
    public async Task AuthenticateAsMaster_SendsFreshNonceEachTime()
    {
        var service = new PairingService();

        var firstNonce = await RunMasterAndCaptureNonceAsync(service);
        var secondNonce = await RunMasterAndCaptureNonceAsync(service);

        Assert.False(firstNonce.SequenceEqual(secondNonce), "the master reused a nonce across handshakes.");
    }

    private static async Task<byte[]> RunMasterAndCaptureNonceAsync(PairingService service)
    {
        var (masterChannel, peerChannel) = InMemoryControlChannel.CreatePair();
        var masterTask = service.AuthenticateAsMasterAsync(masterChannel, Code, Binding, CancellationToken.None);

        var key = await ScriptedAgentJPakeAsync(peerChannel, Code);
        var challenge = Assert.IsType<PairChallengeRequest>(await ReceiveAsync(peerChannel));

        var agentNonce = PairingCrypto.CreateNonce();
        await peerChannel.SendAsync(
            new PairChallengeResponse(
                PairingCrypto.ComputeAgentProof(key, challenge.MasterNonce, agentNonce, Binding), agentNonce),
            CancellationToken.None);

        Assert.True(await WithinDeadlineAsync(masterTask));

        var ack = Assert.IsType<PairAck>(await ReceiveAsync(peerChannel));
        Assert.True(ack.Success);
        Assert.True(PairingCrypto.VerifyMasterProof(key, challenge.MasterNonce, agentNonce, Binding, ack.MasterProof));

        return challenge.MasterNonce;
    }

    private static Task<bool> StartUnderTest(string role, IControlChannel channel, byte[] code) =>
        role == "master"
            ? new PairingService().AuthenticateAsMasterAsync(channel, code, Binding, CancellationToken.None)
            : new PairingService().AuthenticateAsAgentAsync(channel, code, Binding, CancellationToken.None);

    /// <summary>
    /// A peer that plays the master's half of J-PAKE: send round 1, take the agent's, send round 2,
    /// take the agent's. Returns the session key it ends up with, which equals the agent's only if
    /// the codes match.
    /// </summary>
    private static async Task<byte[]> ScriptedMasterJPakeAsync(IControlChannel peer, byte[] code)
    {
        var exchange = new JPakeExchange(JPakeRole.Master, code);

        await peer.SendAsync(exchange.CreateRound1(), CancellationToken.None);
        Assert.True(exchange.TryAcceptRound1(Assert.IsType<PairJPakeRound1>(await ReceiveAsync(peer))));
        await peer.SendAsync(exchange.CreateRound2(), CancellationToken.None);
        Assert.True(exchange.TryAcceptRound2(Assert.IsType<PairJPakeRound2>(await ReceiveAsync(peer))));

        return exchange.DeriveSessionKey();
    }

    /// <summary>The agent's half: take the master's round 1, send round 1, take round 2, send round 2.</summary>
    private static async Task<byte[]> ScriptedAgentJPakeAsync(IControlChannel peer, byte[] code)
    {
        var exchange = new JPakeExchange(JPakeRole.Agent, code);

        // BouncyCastle wants an agent's own round created before the peer's is validated.
        var peerRound1 = Assert.IsType<PairJPakeRound1>(await ReceiveAsync(peer));
        var round1 = exchange.CreateRound1();
        Assert.True(exchange.TryAcceptRound1(peerRound1));
        await peer.SendAsync(round1, CancellationToken.None);

        var peerRound2 = Assert.IsType<PairJPakeRound2>(await ReceiveAsync(peer));
        var round2 = exchange.CreateRound2();
        Assert.True(exchange.TryAcceptRound2(peerRound2));
        await peer.SendAsync(round2, CancellationToken.None);

        return exchange.DeriveSessionKey();
    }

    private static void AssertRejected(ControlMessage message)
    {
        var ack = Assert.IsType<PairAck>(message);
        Assert.False(ack.Success);
        Assert.Null(ack.MasterProof);
    }

    /// <summary>A scripted peer that waits forever on a service that went quiet is a failure, not a hang.</summary>
    private static async Task<ControlMessage> ReceiveAsync(IControlChannel channel)
    {
        using var cts = new CancellationTokenSource(NoHang);
        return await channel.ReceiveAsync(cts.Token);
    }

    /// <summary>A handshake half that hangs is a failure, not a test that runs forever.</summary>
    private static async Task<bool> WithinDeadlineAsync(Task<bool> half)
    {
        var completed = await Task.WhenAny(half, Task.Delay(NoHang));
        Assert.Same(half, completed);
        return await half;
    }

    /// <summary>Sees everything one side sends and receives, without changing any of it.</summary>
    private sealed class RecordingChannel(IControlChannel inner) : IControlChannel
    {
        public List<ControlMessage> Sent { get; } = [];

        public List<ControlMessage> Received { get; } = [];

        public Task SendAsync(ControlMessage message, CancellationToken ct)
        {
            Sent.Add(message);
            return inner.SendAsync(message, ct);
        }

        public async Task<ControlMessage> ReceiveAsync(CancellationToken ct)
        {
            var message = await inner.ReceiveAsync(ct);
            Received.Add(message);
            return message;
        }
    }
}
