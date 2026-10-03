using System.Buffers;
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
            QuicPairingHandshake.RunAsMasterAsync(disposed, PresharedKey, CancellationToken.None));
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            QuicPairingHandshake.RunAsAgentAsync(disposed, PresharedKey, CancellationToken.None));
    }

    /// <summary>Any other InvalidOperationException is a bug in this process too.</summary>
    [Fact]
    public async Task Handshake_PropagatesInvalidOperationException_RatherThanReportingAFailedPairing()
    {
        var broken = new FaultingControlChannel(new InvalidOperationException("not linked"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            QuicPairingHandshake.RunAsAgentAsync(broken, PresharedKey, CancellationToken.None));
    }

    /// <summary>A locally aborted QUIC operation is this process tearing itself down, not the peer.</summary>
    [Fact]
    public async Task Handshake_PropagatesALocallyAbortedOperation()
    {
        var aborted = new FaultingControlChannel(
            new QuicException(QuicError.OperationAborted, null, "aborted locally"));

        await Assert.ThrowsAsync<QuicException>(() =>
            QuicPairingHandshake.RunAsAgentAsync(aborted, PresharedKey, CancellationToken.None));
    }

    /// <summary>Everything the unauthenticated peer can cause is a failed pairing, never a throw.</summary>
    [Fact]
    public async Task Handshake_ReportsAFailedPairing_ForEveryFailureThePeerCanCause()
    {
        Exception[] peerCaused =
        [
            new ControlChannelClosedException("closed mid-handshake"),
            new ControlMessageFormatException("corrupt frame"),
            new QuicException(QuicError.ConnectionAborted, 0, "peer closed the connection"),
            new QuicException(QuicError.StreamAborted, 3, "peer aborted the stream"),
            new QuicException(QuicError.ConnectionIdle, null, "peer went away"),
        ];

        foreach (var failure in peerCaused)
        {
            var channel = new FaultingControlChannel(failure);
            Assert.False(await QuicPairingHandshake.RunAsMasterAsync(channel, PresharedKey, CancellationToken.None));
            Assert.False(await QuicPairingHandshake.RunAsAgentAsync(channel, PresharedKey, CancellationToken.None));
        }
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
        await rawStream.WriteAsync(
            ControlMessageCodec.Encode(new PairChallengeRequest(PairingCrypto.CreateNonce())), deadline.Token);

        await using var agentControl = await agent.AcceptControlChannelAsync(deadline.Token);
        var agentTask = QuicPairingHandshake.RunAsAgentAsync(agentControl, PresharedKey, deadline.Token);

        // Read the agent's answer off the wire, then claim success with a proof that is not one.
        var answer = new byte[4096];
        var read = await rawStream.ReadAsync(answer, deadline.Token);
        Assert.True(ControlMessageCodec.TryDecode(
            new ReadOnlySequence<byte>(answer.AsMemory(0, read)), out var decoded, out _));
        Assert.IsType<PairChallengeResponse>(decoded);

        await rawStream.WriteAsync(
            ControlMessageCodec.Encode(new PairAck(Success: true, new byte[PairingCrypto.ProofLength])),
            deadline.Token);

        Assert.False(await agentTask);

        var aborted = await Assert.ThrowsAsync<QuicException>(
            async () => await rawStream.ReadAsync(answer, deadline.Token));
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
