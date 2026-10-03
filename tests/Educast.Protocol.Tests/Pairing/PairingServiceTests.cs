using Educast.Protocol.Messages;
using Educast.Protocol.Pairing;
using Educast.Protocol.Tests.TestDoubles;

namespace Educast.Protocol.Tests.Pairing;

/// <summary>
/// The mutual four-message handshake:
/// <code>
/// master -> PairChallengeRequest(masterNonce)
/// agent  -> PairChallengeResponse(agentProof, agentNonce)
/// master -> PairAck(masterProof)
/// agent  -> verifies masterProof
/// </code>
/// Roles are fixed by the transport: the master opens the control stream, so the master speaks
/// first and the agent waits to receive.
/// </summary>
public class PairingServiceTests
{
    private static readonly TimeSpan NoHang = TimeSpan.FromSeconds(5);

    /// <summary>
    /// What both sides pass when they are looking at the same two certificates. Opaque to the
    /// service; the transport supplies SHA256(masterCertDER) || SHA256(agentCertDER).
    /// </summary>
    private static readonly byte[] Binding = Enumerable.Range(0, 64).Select(i => (byte)(i + 1)).ToArray();

    [Fact]
    public async Task AuthenticateBothSides_SucceedWhenPresharedKeysMatch()
    {
        var (masterChannel, agentChannel) = InMemoryControlChannel.CreatePair();
        var service = new PairingService();
        byte[] key = [4, 2, 4, 2];

        var masterTask = service.AuthenticateAsMasterAsync(masterChannel, key, Binding, CancellationToken.None);
        var agentTask = service.AuthenticateAsAgentAsync(agentChannel, key, Binding, CancellationToken.None);
        var results = await Task.WhenAll(masterTask, agentTask);

        Assert.All(results, succeeded => Assert.True(succeeded));
    }

    [Fact]
    public async Task AuthenticateBothSides_FailWhenPresharedKeysDiffer()
    {
        var (masterChannel, agentChannel) = InMemoryControlChannel.CreatePair();
        var service = new PairingService();

        var masterTask = service.AuthenticateAsMasterAsync(masterChannel, [1, 1, 1, 1], Binding, CancellationToken.None);
        var agentTask = service.AuthenticateAsAgentAsync(agentChannel, [2, 2, 2, 2], Binding, CancellationToken.None);
        var results = await Task.WhenAll(masterTask, agentTask);

        Assert.All(results, succeeded => Assert.False(succeeded));
    }

    /// <summary>
    /// The relay, at the protocol level. Both sides hold the right key, and every message is
    /// delivered unchanged — but each side was handed a different binding, which is what happens
    /// when each is looking at a middle party's certificate instead of the other's. Neither proof
    /// verifies, so neither side pairs. Fails proofs that cover only the nonces.
    /// </summary>
    [Fact]
    public async Task AuthenticateBothSides_Fail_WhenTheyAreNotLookingAtTheSameCertificates()
    {
        var (masterChannel, agentChannel) = InMemoryControlChannel.CreatePair();
        var service = new PairingService();
        byte[] key = [4, 2, 4, 2];
        var seenByAgent = (byte[])Binding.Clone();
        seenByAgent[0] ^= 0xFF;

        var masterTask = service.AuthenticateAsMasterAsync(masterChannel, key, Binding, CancellationToken.None);
        var agentTask = service.AuthenticateAsAgentAsync(agentChannel, key, seenByAgent, CancellationToken.None);

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
            masterChannel, [1, 2, 3, 4], ReadOnlyMemory<byte>.Empty, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => service.AuthenticateAsAgentAsync(
            agentChannel, [1, 2, 3, 4], ReadOnlyMemory<byte>.Empty, CancellationToken.None));
    }

    /// <summary>
    /// The oracle, made concrete. A rogue master sends a nonce of its choosing, harvests the
    /// agent's answer, and claims success without ever proving the pre-shared key. The agent must
    /// refuse — this is the test the one-directional handshake fails, and its <c>false</c> is what
    /// keeps the rogue's certificate from being pinned.
    /// </summary>
    [Theory]
    [InlineData("none")]
    [InlineData("garbage")]
    [InlineData("reflected")]
    public async Task AuthenticateAsAgent_Fails_WhenTheMasterNeverProvesTheKey(string forgery)
    {
        var (roguePeer, agentChannel) = InMemoryControlChannel.CreatePair();
        byte[] key = [7, 7, 7, 7];

        var agentTask = new PairingService().AuthenticateAsAgentAsync(agentChannel, key, Binding, CancellationToken.None);

        // A nonce the rogue chose, not one it was given.
        byte[] chosenNonce = [.. Enumerable.Repeat((byte)0xAB, PairingCrypto.NonceLength)];
        await roguePeer.SendAsync(new PairChallengeRequest(chosenNonce), CancellationToken.None);

        var reply = Assert.IsType<PairChallengeResponse>(
            await roguePeer.ReceiveAsync(CancellationToken.None));

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
    /// different value under a different label, and only the pre-shared key produces it.
    /// </summary>
    [Fact]
    public async Task AuthenticateAsAgent_Succeeds_OnlyForAProofComputedWithTheKey()
    {
        var (masterChannel, agentChannel) = InMemoryControlChannel.CreatePair();
        byte[] key = [7, 7, 7, 7];

        var agentTask = new PairingService().AuthenticateAsAgentAsync(agentChannel, key, Binding, CancellationToken.None);

        byte[] masterNonce = PairingCrypto.CreateNonce();
        await masterChannel.SendAsync(new PairChallengeRequest(masterNonce), CancellationToken.None);
        var reply = Assert.IsType<PairChallengeResponse>(
            await masterChannel.ReceiveAsync(CancellationToken.None));

        // The agent's proof must itself be the specified value over both nonces.
        Assert.True(PairingCrypto.VerifyAgentProof(key, masterNonce, reply.AgentNonce, Binding, reply.AgentProof));

        await masterChannel.SendAsync(
            new PairAck(Success: true, PairingCrypto.ComputeMasterProof(key, masterNonce, reply.AgentNonce, Binding)),
            CancellationToken.None);

        Assert.True(await WithinDeadlineAsync(agentTask));
    }

    /// <summary>
    /// A malformed nonce is peer-controlled input: a clean false, never an exception, and never a
    /// proof computed over it.
    /// </summary>
    [Fact]
    public async Task AuthenticateAsAgent_Fails_WhenTheMasterNonceIsTheWrongLength()
    {
        var (peerChannel, agentChannel) = InMemoryControlChannel.CreatePair();

        var agentTask = new PairingService().AuthenticateAsAgentAsync(
            agentChannel, [1, 2, 3, 4], Binding, CancellationToken.None);
        await peerChannel.SendAsync(new PairChallengeRequest([1, 2, 3]), CancellationToken.None);

        Assert.False(await WithinDeadlineAsync(agentTask));
        Assert.False(Assert.IsType<PairAck>(await peerChannel.ReceiveAsync(CancellationToken.None)).Success);
    }

    [Fact]
    public async Task AuthenticateAsMaster_Fails_WhenTheAgentNonceIsTheWrongLength()
    {
        var (masterChannel, peerChannel) = InMemoryControlChannel.CreatePair();
        byte[] key = [1, 2, 3, 4];

        var masterTask = new PairingService().AuthenticateAsMasterAsync(
            masterChannel, key, Binding, CancellationToken.None);

        var challenge = Assert.IsType<PairChallengeRequest>(
            await peerChannel.ReceiveAsync(CancellationToken.None));
        byte[] shortNonce = [1, 2, 3];
        await peerChannel.SendAsync(
            new PairChallengeResponse(PairingCrypto.ComputeAgentProof(key, challenge.MasterNonce, shortNonce, Binding), shortNonce),
            CancellationToken.None);

        Assert.False(await WithinDeadlineAsync(masterTask));
        Assert.False(Assert.IsType<PairAck>(await peerChannel.ReceiveAsync(CancellationToken.None)).Success);
    }

    /// <summary>
    /// The agent must SIGNAL a peer it is giving up on. Returning silently leaves the master
    /// blocked on a receive until its deadline expires, which is the Task 3 review requirement
    /// this pins.
    /// </summary>
    [Fact]
    public async Task AuthenticateAsAgent_SignalsThePeer_WhenTheFirstMessageIsNotAChallenge()
    {
        var (peerChannel, agentChannel) = InMemoryControlChannel.CreatePair();

        await peerChannel.SendAsync(new StopBroadcast(), CancellationToken.None);
        var agentTask = new PairingService().AuthenticateAsAgentAsync(
            agentChannel, [1, 2, 3, 4], Binding, CancellationToken.None);

        Assert.False(await WithinDeadlineAsync(agentTask));

        // The peer is told, rather than left waiting.
        var signal = Assert.IsType<PairAck>(await peerChannel.ReceiveAsync(CancellationToken.None));
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
            masterChannel, [1, 2, 3, 4], Binding, CancellationToken.None);

        Assert.IsType<PairChallengeRequest>(await peerChannel.ReceiveAsync(CancellationToken.None));
        await peerChannel.SendAsync(new BroadcastReady(), CancellationToken.None);

        Assert.False(await WithinDeadlineAsync(masterTask));

        var signal = Assert.IsType<PairAck>(await peerChannel.ReceiveAsync(CancellationToken.None));
        Assert.False(signal.Success);
        Assert.Null(signal.MasterProof);
    }

    /// <summary>A forged agent proof is refused, and the agent is told so.</summary>
    [Fact]
    public async Task AuthenticateAsMaster_Fails_WhenTheAgentProofIsForged()
    {
        var (masterChannel, peerChannel) = InMemoryControlChannel.CreatePair();

        var masterTask = new PairingService().AuthenticateAsMasterAsync(
            masterChannel, [1, 2, 3, 4], Binding, CancellationToken.None);

        Assert.IsType<PairChallengeRequest>(await peerChannel.ReceiveAsync(CancellationToken.None));
        await peerChannel.SendAsync(
            new PairChallengeResponse(new byte[PairingCrypto.ProofLength], PairingCrypto.CreateNonce()),
            CancellationToken.None);

        Assert.False(await WithinDeadlineAsync(masterTask));
        Assert.False(Assert.IsType<PairAck>(await peerChannel.ReceiveAsync(CancellationToken.None)).Success);
    }

    [Fact]
    public async Task AuthenticateAsMaster_SendsFreshNonceEachTime()
    {
        var service = new PairingService();
        byte[] key = [7, 7, 7, 7];

        var firstNonce = await RunMasterAndCaptureNonceAsync(service, key);
        var secondNonce = await RunMasterAndCaptureNonceAsync(service, key);

        Assert.False(firstNonce.SequenceEqual(secondNonce), "the master reused a nonce across handshakes.");
    }

    private static async Task<byte[]> RunMasterAndCaptureNonceAsync(PairingService service, byte[] key)
    {
        var (masterChannel, peerChannel) = InMemoryControlChannel.CreatePair();
        var masterTask = service.AuthenticateAsMasterAsync(masterChannel, key, Binding, CancellationToken.None);

        var challenge = Assert.IsType<PairChallengeRequest>(
            await peerChannel.ReceiveAsync(CancellationToken.None));

        var agentNonce = PairingCrypto.CreateNonce();
        await peerChannel.SendAsync(
            new PairChallengeResponse(
                PairingCrypto.ComputeAgentProof(key, challenge.MasterNonce, agentNonce, Binding), agentNonce),
            CancellationToken.None);

        Assert.True(await WithinDeadlineAsync(masterTask));

        var ack = Assert.IsType<PairAck>(await peerChannel.ReceiveAsync(CancellationToken.None));
        Assert.True(ack.Success);
        Assert.True(PairingCrypto.VerifyMasterProof(key, challenge.MasterNonce, agentNonce, Binding, ack.MasterProof));

        return challenge.MasterNonce;
    }

    /// <summary>A handshake half that hangs is a failure, not a test that runs forever.</summary>
    private static async Task<bool> WithinDeadlineAsync(Task<bool> half)
    {
        var completed = await Task.WhenAny(half, Task.Delay(NoHang));
        Assert.Same(half, completed);
        return await half;
    }
}
