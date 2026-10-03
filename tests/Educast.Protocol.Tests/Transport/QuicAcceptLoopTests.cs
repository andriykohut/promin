using System.Net.Quic;
using System.Security.Authentication;
using Educast.Protocol.Transport;

namespace Educast.Protocol.Tests.Transport;

/// <summary>
/// The agent's accept loop under hostile and broken conditions. The loop is what keeps a
/// student PC on the air, so it has to survive strangers, survive its own observers, never
/// spin, and never hide a fault that is this machine's own.
/// </summary>
public class QuicAcceptLoopTests
{
    // ---- Over real QUIC -----------------------------------------------------------------

    /// <summary>
    /// The rejection callback is the pairing UI's. If it throws, that is the UI's bug and must
    /// not take the listener down with it. Fails an accept loop that invokes the callback
    /// unguarded — there, the callback's exception comes out of AcceptAsync and the agent stops
    /// accepting anyone.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task AcceptLoop_Survives_ARejectionCallbackThatThrows()
    {
        using var deadline = QuicLoopbackHarness.Deadline();
        var reported = 0;
        await using var harness = await QuicLoopbackHarness.StartAsync(
            deadline.Token,
            connectionRejected: _ =>
            {
                Interlocked.Increment(ref reported);
                throw new InvalidOperationException("the pairing UI fell over");
            });

        var acceptTask = harness.Listener.AcceptAsync(deadline.Token);

        using var strangerCertificate = SelfSignedCertificateFactory.Create("master-stranger");
        var strangerTrust = new PeerCertificateStore();
        strangerTrust.Pin(
            QuicLoopbackHarness.AgentDeviceId,
            PeerCertificateStore.ComputeThumbprint(harness.AgentCertificate));
        var stranger = new QuicMasterConnector(strangerCertificate, strangerTrust);
        Assert.NotNull(await QuicLoopbackHarness.ProbeConnectionAsync(
            stranger, harness.Endpoint, QuicLoopbackHarness.AgentDeviceId, deadline.Token));

        // The legitimate master still gets through, on the very accept that saw the stranger.
        var connector = new QuicMasterConnector(harness.MasterCertificate, harness.MasterTrust);
        await using var master = await connector.ConnectAsync(
            harness.Endpoint, QuicLoopbackHarness.AgentDeviceId, deadline.Token);
        await using var agent = await acceptTask;

        Assert.True(Volatile.Read(ref reported) >= 1, "the rejection was never reported");
    }

    /// <summary>
    /// Many strangers in a row, then the real master. Fails a loop that gives up, or dies, after
    /// some number of consecutive rejections — which would let anyone on the LAN take an agent
    /// off the air by dialling it a few times.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task AcceptLoop_KeepsServing_AfterManyConsecutiveRejections()
    {
        using var deadline = QuicLoopbackHarness.Deadline(40);
        var reported = 0;
        await using var harness = await QuicLoopbackHarness.StartAsync(
            deadline.Token, connectionRejected: _ => Interlocked.Increment(ref reported));

        var acceptTask = harness.Listener.AcceptAsync(deadline.Token);

        const int strangers = 6;
        for (var i = 0; i < strangers; i++)
        {
            using var strangerCertificate = SelfSignedCertificateFactory.Create($"master-stranger-{i}");
            var strangerTrust = new PeerCertificateStore();
            strangerTrust.Pin(
                QuicLoopbackHarness.AgentDeviceId,
                PeerCertificateStore.ComputeThumbprint(harness.AgentCertificate));
            Assert.NotNull(await QuicLoopbackHarness.ProbeConnectionAsync(
                new QuicMasterConnector(strangerCertificate, strangerTrust),
                harness.Endpoint, QuicLoopbackHarness.AgentDeviceId, deadline.Token));
        }

        var connector = new QuicMasterConnector(harness.MasterCertificate, harness.MasterTrust);
        await using var master = await connector.ConnectAsync(
            harness.Endpoint, QuicLoopbackHarness.AgentDeviceId, deadline.Token);
        await using var agent = await acceptTask;

        Assert.Equal(strangers, Volatile.Read(ref reported));
        Assert.Equal(0, harness.Listener.ConsecutiveAcceptFailures);
    }

    // ---- The pump on its own, with an accept source that can be made to fail on demand ----

    /// <summary>
    /// An accept that fails instantly, every time, must not become a CPU-burning loop. Before,
    /// the loop was `catch … continue` with no delay: this accept source would be called
    /// millions of times in the second the test runs. With a backoff it is called a handful.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task Pump_BacksOff_WhenAcceptFailsPersistently()
    {
        var attempts = 0;
        var reported = 0;
        var pump = new QuicAcceptPump(
            accept: _ =>
            {
                Interlocked.Increment(ref attempts);
                throw new AuthenticationException("refused");
            },
            deliver: (_, _) => ValueTask.CompletedTask,
            connectionRejected: _ => Interlocked.Increment(ref reported));

        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        await pump.RunAsync(stop.Token);

        // 0, 0, 25, 50, 100, 200, 400, 500 ms: eight attempts fit in a second, give or take one.
        Assert.InRange(Volatile.Read(ref attempts), 3, 12);
        Assert.Equal(Volatile.Read(ref attempts), Volatile.Read(ref reported));
        Assert.Equal(Volatile.Read(ref attempts), pump.ConsecutiveFailures);
    }

    /// <summary>A callback that throws on every rejection does not stop the pump either.</summary>
    [Fact(Timeout = 30000)]
    public async Task Pump_KeepsRunning_WhenTheRejectionCallbackThrowsEveryTime()
    {
        var attempts = 0;
        var parked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pump = new QuicAcceptPump(
            accept: async ct =>
            {
                if (Interlocked.Increment(ref attempts) <= 3)
                {
                    throw new AuthenticationException("refused");
                }

                parked.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);
                throw new InvalidOperationException("unreachable");
            },
            deliver: (_, _) => ValueTask.CompletedTask,
            connectionRejected: _ => throw new InvalidOperationException("observer fell over"));

        using var stop = new CancellationTokenSource();
        var running = pump.RunAsync(stop.Token);

        await parked.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(running.IsCompleted, "the pump died on a throwing callback");

        await stop.CancelAsync();
        await running;
    }

    /// <summary>
    /// A fault of this machine's own is not "a peer was rejected". It must come out of the pump
    /// so the owner sees it, instead of being absorbed forever. Fails an unconditional
    /// catch-and-continue.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task Pump_Surfaces_ALocalFault_InsteadOfAbsorbingIt()
    {
        Exception[] localFaults =
        [
            new QuicException(QuicError.InternalError, null, "ConfigurationLoadCredential failed"),
            new QuicException(QuicError.CallbackError, null, "the options callback threw"),
            new InvalidOperationException("not a handshake failure at all"),
        ];

        foreach (var fault in localFaults)
        {
            var reported = 0;
            var pump = new QuicAcceptPump(
                accept: _ => throw fault,
                deliver: (_, _) => ValueTask.CompletedTask,
                connectionRejected: _ => Interlocked.Increment(ref reported));

            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var thrown = await Assert.ThrowsAnyAsync<Exception>(() => pump.RunAsync(stop.Token));

            Assert.Same(fault, thrown);
            Assert.Equal(0, reported);
        }
    }

    [Fact]
    public void PeerHandshakeFailures_AreExactlyTheOnesAPeerCanCause()
    {
        Assert.True(QuicAcceptPump.IsPeerHandshakeFailure(new AuthenticationException("bad certificate")));
        foreach (var peerCaused in new[]
        {
            QuicError.ConnectionAborted, QuicError.ConnectionTimeout, QuicError.ConnectionRefused,
            QuicError.ConnectionIdle, QuicError.VersionNegotiationError, QuicError.TransportError,
        })
        {
            Assert.True(
                QuicAcceptPump.IsPeerHandshakeFailure(new QuicException(peerCaused, null, "x")),
                $"{peerCaused} is caused by the peer and must not stop the listener");
        }

        foreach (var local in new[]
        {
            QuicError.InternalError, QuicError.CallbackError, QuicError.OperationAborted,
            QuicError.AlpnInUse, QuicError.StreamAborted,
        })
        {
            Assert.False(
                QuicAcceptPump.IsPeerHandshakeFailure(new QuicException(local, null, "x")),
                $"{local} is not a peer rejection and must not be absorbed as one");
        }

        Assert.False(QuicAcceptPump.IsPeerHandshakeFailure(new ObjectDisposedException("listener")));
        Assert.False(QuicAcceptPump.IsPeerHandshakeFailure(new InvalidOperationException()));
        Assert.False(QuicAcceptPump.IsPeerHandshakeFailure(new OperationCanceledException()));
    }

    /// <summary>One or two strangers cost nothing; a run of failures is slowed; the delay is capped.</summary>
    [Fact]
    public void Backoff_IsZeroForIsolatedFailures_ThenGrows_ThenIsCapped()
    {
        Assert.Equal(TimeSpan.Zero, QuicAcceptPump.BackoffAfter(0));
        Assert.Equal(TimeSpan.Zero, QuicAcceptPump.BackoffAfter(1));
        Assert.Equal(TimeSpan.Zero, QuicAcceptPump.BackoffAfter(2));
        Assert.Equal(TimeSpan.FromMilliseconds(25), QuicAcceptPump.BackoffAfter(3));
        Assert.Equal(TimeSpan.FromMilliseconds(50), QuicAcceptPump.BackoffAfter(4));
        Assert.Equal(TimeSpan.FromMilliseconds(400), QuicAcceptPump.BackoffAfter(7));
        Assert.Equal(QuicAcceptPump.MaxBackoff, QuicAcceptPump.BackoffAfter(8));
        Assert.Equal(QuicAcceptPump.MaxBackoff, QuicAcceptPump.BackoffAfter(int.MaxValue));
        Assert.Equal(TimeSpan.FromMilliseconds(500), QuicAcceptPump.MaxBackoff);
    }
}
