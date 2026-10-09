using System.Net.Quic;
using Educast.Protocol.Messages;
using Educast.Protocol.Pairing;
using Educast.Protocol.Transport;

namespace Educast.Protocol.Tests.Transport;

/// <summary>
/// What the deadline-bounded handshake wrapper turns into "pairing failed" and — just as
/// important — what it refuses to.
/// </summary>
public class QuicPairingHandshakeTests
{
    private static readonly byte[] PresharedKey = [7, 7, 7, 7];

    /// <summary>Any 64 bytes: these tests never get as far as a proof.</summary>
    private static readonly byte[] Binding = new byte[CertificateBinding.Length];

    /// <summary>
    /// A disposed channel is a lifecycle bug in this process, not a peer that failed to pair.
    /// ObjectDisposedException derives from InvalidOperationException, so catching the latter
    /// reported it as a clean "pairing failed" and hid the bug. Fails that catch.
    /// </summary>
    [Fact]
    public async Task Handshake_PropagatesObjectDisposedException_RatherThanReportingAFailedPairing()
    {
        var disposed = new FaultingControlChannel(new ObjectDisposedException("QuicControlChannel"));

        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            QuicPairingHandshake.RunAsMasterAsync(disposed, PresharedKey, Binding, CancellationToken.None));
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            QuicPairingHandshake.RunAsAgentAsync(disposed, PresharedKey, Binding, CancellationToken.None));
    }

    /// <summary>Any other InvalidOperationException is a bug in this process too.</summary>
    [Fact]
    public async Task Handshake_PropagatesInvalidOperationException_RatherThanReportingAFailedPairing()
    {
        var broken = new FaultingControlChannel(new InvalidOperationException("not linked"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            QuicPairingHandshake.RunAsAgentAsync(broken, PresharedKey, Binding, CancellationToken.None));
    }

    /// <summary>A locally aborted QUIC operation is this process tearing itself down, not the peer.</summary>
    [Fact]
    public async Task Handshake_PropagatesALocallyAbortedOperation()
    {
        var aborted = new FaultingControlChannel(
            new QuicException(QuicError.OperationAborted, null, "aborted locally"));

        await Assert.ThrowsAsync<QuicException>(() =>
            QuicPairingHandshake.RunAsAgentAsync(aborted, PresharedKey, Binding, CancellationToken.None));
    }

    /// <summary>
    /// Everything the unauthenticated peer can cause is a failed pairing, never a throw — and
    /// the result says which kind, so the operator can be told the truth about it.
    /// </summary>
    [Fact]
    public async Task Handshake_ReportsAFailedPairing_ForEveryFailureThePeerCanCause()
    {
        (Exception Failure, PairingHandshakeResult Expected)[] peerCaused =
        [
            (new ControlChannelClosedException("closed mid-handshake"), PairingHandshakeResult.PeerClosed),
            (new ControlMessageFormatException("corrupt frame"), PairingHandshakeResult.CorruptFrame),
            (new QuicException(QuicError.ConnectionAborted, 0, "peer closed the connection"),
                PairingHandshakeResult.PeerClosed),
            (new QuicException(QuicError.StreamAborted, 3, "peer aborted the stream"),
                PairingHandshakeResult.PeerClosed),
            (new QuicException(QuicError.ConnectionIdle, null, "peer went away"), PairingHandshakeResult.PeerClosed),

            // The peer refusing this side's certificate: TLS 1.3 delivers that after the
            // connection is up, on whatever operation happens to be in flight.
            (new System.Security.Authentication.AuthenticationException("TLS alert: BadCertificate"),
                PairingHandshakeResult.PeerRefused),
        ];

        foreach (var (failure, expected) in peerCaused)
        {
            var channel = new FaultingControlChannel(failure);
            Assert.Equal(
                expected,
                await QuicPairingHandshake.RunAsMasterAsync(channel, PresharedKey, Binding, CancellationToken.None));
            Assert.Equal(
                expected,
                await QuicPairingHandshake.RunAsAgentAsync(channel, PresharedKey, Binding, CancellationToken.None));
        }
    }

    /// <summary>
    /// The handshake's own deadline, and the caller's. Neither may leave the stream to close in
    /// good order, because the peer would read that as the handshake having finished. When the
    /// caller's own token is the one that fires — which is what happens when an outer deadline
    /// shares the budget and started first — the cancellation still propagates, and the stream
    /// is still aborted on the way out. Fails an abort that only runs on the "returned false"
    /// path.
    /// </summary>
    [Theory(Timeout = 30000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ATimedOutHandshake_AbortsTheControlStream_WhicheverDeadlineFired(bool callersTokenFires)
    {
        using var deadline = QuicLoopbackHarness.Deadline();
        await using var harness = await QuicLoopbackHarness.StartAsync(deadline.Token);

        var acceptTask = harness.Listener.AcceptAsync(deadline.Token);
        var rawMaster = await harness.ConnectRawAsync(deadline.Token);
        await using var agent = await acceptTask;

        await using var rawStream = await rawMaster.OpenOutboundStreamAsync(
            QuicStreamType.Bidirectional, deadline.Token);
        var rawJPake = await RawPairing.StartMasterJPakeAsync(rawStream, PresharedKey, deadline.Token);
        await using var agentControl = await agent.AcceptControlChannelAsync(deadline.Token);

        // The master answers nothing once the J-PAKE rounds and its challenge are done, so the
        // agent's handshake runs out of time. The budget has to cover the rounds' arithmetic.
        using var callers = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        callers.CancelAfter(callersTokenFires ? TimeSpan.FromSeconds(2) : TimeSpan.FromSeconds(20));
        var agentTask = QuicPairingHandshake.RunAsAgentAsync(
            agentControl,
            PresharedKey,
            harness.Binding,
            callers.Token,
            callersTokenFires ? TimeSpan.FromSeconds(20) : TimeSpan.FromSeconds(2));

        await rawJPake.FinishAsync(deadline.Token);
        await RawPairing.WriteAsync(rawStream, new PairChallengeRequest(PairingCrypto.CreateNonce()), deadline.Token);
        Assert.IsType<PairChallengeResponse>(await RawPairing.ReadAsync(rawStream, deadline.Token));

        if (callersTokenFires)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => agentTask);
        }
        else
        {
            Assert.Equal(PairingHandshakeResult.TimedOut, await agentTask);
        }

        var aborted = await Assert.ThrowsAsync<QuicException>(() => RawPairing.ReadAsync(rawStream, deadline.Token));
        Assert.Equal(QuicError.StreamAborted, aborted.QuicError);
        Assert.Equal(QuicControlChannel.PairingFailedErrorCode, aborted.ApplicationErrorCode);
    }

    /// <summary>
    /// After a failed handshake the control stream must not be left half-finished: the peer has
    /// to see it end. Here a rogue master harvests the agent's answer and never proves the key;
    /// the agent refuses, and the rogue's next read must fail with the pairing-failed abort code
    /// instead of blocking on a stream nobody will ever write to again. Fails an implementation
    /// that returns false and walks away from the stream.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task FailedHandshake_AbortsTheControlStream_SoThePeerIsNotLeftWaiting()
    {
        using var deadline = QuicLoopbackHarness.Deadline();
        await using var harness = await QuicLoopbackHarness.StartAsync(deadline.Token);

        var acceptTask = harness.Listener.AcceptAsync(deadline.Token);
        var rawMaster = await harness.ConnectRawAsync(deadline.Token);
        await using var agent = await acceptTask;

        await using var rawStream = await rawMaster.OpenOutboundStreamAsync(
            QuicStreamType.Bidirectional, deadline.Token);
        var rawJPake = await RawPairing.StartMasterJPakeAsync(rawStream, PresharedKey, deadline.Token);

        await using var agentControl = await agent.AcceptControlChannelAsync(deadline.Token);
        var agentTask = harness.HandshakeAsAgentAsync(agentControl, PresharedKey, deadline.Token);

        // Take the agent's answer to a challenge, then claim success with a proof that is not one.
        await rawJPake.FinishAsync(deadline.Token);
        await RawPairing.WriteAsync(rawStream, new PairChallengeRequest(PairingCrypto.CreateNonce()), deadline.Token);
        Assert.IsType<PairChallengeResponse>(await RawPairing.ReadAsync(rawStream, deadline.Token));

        var answer = new byte[4096];
        await rawStream.WriteAsync(
            ControlMessageCodec.Encode(new PairAck(Success: true, new byte[PairingCrypto.ProofLength])),
            deadline.Token);

        Assert.False(await agentTask);

        var aborted = await Assert.ThrowsAsync<QuicException>(
            async () => await rawStream.ReadAtLeastAsync(answer, 1, cancellationToken: deadline.Token));
        Assert.Equal(QuicError.StreamAborted, aborted.QuicError);
        Assert.Equal(QuicControlChannel.PairingFailedErrorCode, aborted.ApplicationErrorCode);
    }

    private sealed class FaultingControlChannel(Exception fault) : IControlChannel
    {
        public Task SendAsync(ControlMessage message, CancellationToken ct) => Task.FromException(fault);

        public Task<ControlMessage> ReceiveAsync(CancellationToken ct) =>
            Task.FromException<ControlMessage>(fault);
    }
}
