using Educast.Protocol.Messages;
using Educast.Protocol.Pairing;
using Educast.Protocol.Tests.TestDoubles;

namespace Educast.Protocol.Tests.Pairing;

public class PairingServiceTests
{
    // Roles, per the corrected design: the verifier opens the exchange by sending the
    // challenge first (it must, because on real QUIC the side that opens the control
    // stream has to write before the peer can see the stream at all). The prover waits
    // to receive the challenge, then answers it. The master is always the verifier; the
    // agent is always the prover.

    [Fact]
    public async Task AuthenticateBothSides_SucceedWhenPresharedKeysMatch()
    {
        var (verifierChannel, proverChannel) = InMemoryControlChannel.CreatePair();
        var service = new PairingService();
        byte[] key = [4, 2, 4, 2];

        var verifierTask = service.AuthenticateAsVerifierAsync(verifierChannel, key, CancellationToken.None);
        var proverTask = service.AuthenticateAsProverAsync(proverChannel, key, CancellationToken.None);
        var results = await Task.WhenAll(verifierTask, proverTask);

        Assert.All(results, succeeded => Assert.True(succeeded));
    }

    [Fact]
    public async Task AuthenticateBothSides_FailWhenPresharedKeysDiffer()
    {
        var (verifierChannel, proverChannel) = InMemoryControlChannel.CreatePair();
        var service = new PairingService();

        var verifierTask = service.AuthenticateAsVerifierAsync(verifierChannel, [1, 1, 1, 1], CancellationToken.None);
        var proverTask = service.AuthenticateAsProverAsync(proverChannel, [2, 2, 2, 2], CancellationToken.None);
        var results = await Task.WhenAll(verifierTask, proverTask);

        Assert.All(results, succeeded => Assert.False(succeeded));
    }

    [Fact]
    public async Task AuthenticateAsProver_FailsCleanly_WhenFirstMessageIsNotAChallenge()
    {
        var (peerChannel, proverChannel) = InMemoryControlChannel.CreatePair();
        var service = new PairingService();

        // A hostile or corrupt peer sends something other than a PairChallengeRequest
        // as the first message. The prover must return false, not throw or hang.
        await peerChannel.SendAsync(new PairAck(Success: true), CancellationToken.None);

        var proverTask = service.AuthenticateAsProverAsync(proverChannel, [1, 2, 3, 4], CancellationToken.None);
        var completed = await Task.WhenAny(proverTask, Task.Delay(TimeSpan.FromSeconds(5)));

        Assert.Same(proverTask, completed);
        Assert.False(await proverTask);
    }

    [Fact]
    public async Task AuthenticateAsVerifier_FailsAndSendsNegativeAck_WhenReplyIsNotAChallengeResponse()
    {
        var (verifierChannel, peerChannel) = InMemoryControlChannel.CreatePair();
        var service = new PairingService();

        var verifierTask = service.AuthenticateAsVerifierAsync(verifierChannel, [1, 2, 3, 4], CancellationToken.None);

        // Drain the challenge the verifier sends first, then reply with an unexpected
        // message type instead of a PairChallengeResponse.
        var challenge = await peerChannel.ReceiveAsync(CancellationToken.None);
        Assert.IsType<PairChallengeRequest>(challenge);
        await peerChannel.SendAsync(new PairAck(Success: true), CancellationToken.None);

        var completed = await Task.WhenAny(verifierTask, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(verifierTask, completed);
        Assert.False(await verifierTask);

        var ack = await peerChannel.ReceiveAsync(CancellationToken.None);
        var pairAck = Assert.IsType<PairAck>(ack);
        Assert.False(pairAck.Success);
    }

    [Fact]
    public async Task AuthenticateAsVerifier_SendsFreshNonceEachTime()
    {
        var service = new PairingService();
        byte[] key = [7, 7, 7, 7];

        var firstNonce = await RunVerifierAndCaptureNonceAsync(service, key);
        var secondNonce = await RunVerifierAndCaptureNonceAsync(service, key);

        Assert.False(firstNonce.SequenceEqual(secondNonce), "Verifier reused a nonce across handshakes.");
    }

    private static async Task<byte[]> RunVerifierAndCaptureNonceAsync(PairingService service, byte[] key)
    {
        var (verifierChannel, peerChannel) = InMemoryControlChannel.CreatePair();
        var verifierTask = service.AuthenticateAsVerifierAsync(verifierChannel, key, CancellationToken.None);

        var challenge = await peerChannel.ReceiveAsync(CancellationToken.None);
        var request = Assert.IsType<PairChallengeRequest>(challenge);

        var response = PairingCrypto.ComputeResponse(key, request.Nonce);
        await peerChannel.SendAsync(new PairChallengeResponse(response), CancellationToken.None);

        var succeeded = await verifierTask;
        Assert.True(succeeded);

        return request.Nonce;
    }
}
