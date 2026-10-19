using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Promin.Protocol.Messages;
using Promin.Protocol.Transport;

namespace Promin.Protocol.Tests.Transport;

/// <summary>
/// The agent's accept loop under hostile and broken conditions. The loop is what keeps a
/// agent PC on the air. Three properties, each checked against the real QUIC runtime rather
/// than against exceptions constructed to look like it:
/// a broken local credential fails <c>StartAsync</c>; nothing a remote peer does stops the loop;
/// and a flood of refused dials neither delays a legitimate master nor spins a core.
/// </summary>
public class QuicAcceptLoopTests(ITestOutputHelper output)
{
    // ---- (a) A local credential fault fails StartAsync, loudly ---------------------------

    /// <summary>
    /// A certificate with no private key cannot complete a handshake. The TLS stack does not say
    /// so when the listener starts — it reports it later, per connection, as an
    /// AuthenticationException out of accept, which is what a refused stranger looks like. The
    /// listener therefore proves its credential against itself before it reports that it has
    /// started. Fails a StartAsync that returns normally and leaves the agent "rejecting"
    /// every master for ever.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task StartAsync_Throws_WhenTheCertificateHasNoPrivateKey()
    {
        using var deadline = QuicLoopbackHarness.Deadline(TestContext.Current.CancellationToken, 30);
        using var whole = SelfSignedCertificateFactory.Create("agent-broken");
        using var publicOnly = X509CertificateLoader.LoadCertificate(whole.RawData);
        Assert.False(publicOnly.HasPrivateKey);

        await AssertStartFailsAsync(publicOnly, deadline.Token);
    }

    /// <summary>
    /// The second fault the review probed: a key the platform cannot use for TLS. On Windows an
    /// ephemeral-key certificate is refused by SChannel.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task StartAsync_Throws_WhenTheCertificateHasAnEphemeralKey()
    {
        using var deadline = QuicLoopbackHarness.Deadline(TestContext.Current.CancellationToken, 30);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=agent-ephemeral", key, HashAlgorithmName.SHA256);
        using var inMemory = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
        using var ephemeral = X509CertificateLoader.LoadPkcs12(
            inMemory.Export(X509ContentType.Pfx), password: null, X509KeyStorageFlags.EphemeralKeySet);
        Assert.True(ephemeral.HasPrivateKey);

        await AssertStartFailsAsync(ephemeral, deadline.Token);
    }

    private async Task AssertStartFailsAsync(X509Certificate2 brokenCertificate, CancellationToken ct)
    {
        var listener = new QuicAgentListener(
            brokenCertificate, new PeerCertificateStore(), port: 0, IPAddress.Loopback);
        await using (listener)
        {
            var started = Stopwatch.StartNew();
            var failure = await Assert.ThrowsAsync<LocalCredentialException>(() => listener.StartAsync(ct));

            output.WriteLine($"StartAsync threw after {started.ElapsedMilliseconds} ms: {failure.Message}");
            output.WriteLine(
                $"  inner: {failure.InnerException?.GetType().Name}: {failure.InnerException?.Message}");
            Assert.NotNull(failure.InnerException);

            // It is not left half-started: nothing is listening and nothing can be accepted.
            await Assert.ThrowsAsync<ObjectDisposedException>(() => listener.AcceptAsync(ct));
        }
    }

    // ---- (b) After a successful start, nothing a remote peer can cause stops the loop -----

    /// <summary>
    /// Every way of misbehaving at a listener that loopback allows: a certificate it has no pin
    /// for, no certificate at all, the wrong protocol, a connection closed with an arbitrary
    /// error code the instant the client's side of the handshake is done, a connection abandoned
    /// without closing, and a handshake cancelled part-way. Whatever the runtime turns each of
    /// those into — the types are written to the test output — the listener is still serving
    /// afterwards: the paired master connects and exchanges a control message. Fails an accept
    /// loop that treats any of them as fatal.
    /// </summary>
    [Fact(Timeout = 120000)]
    public async Task AcceptLoop_KeepsServing_WhateverAPeerDoesToIt()
    {
        using var deadline = QuicLoopbackHarness.Deadline(TestContext.Current.CancellationToken, 90);
        var seen = new ConcurrentQueue<Exception>();
        await using var harness = await QuicLoopbackHarness.StartAsync(
            deadline.Token, connectionRejected: seen.Enqueue);
        using var stranger = SelfSignedCertificateFactory.Create("master-stranger");

        var misbehaviours = new (string Name, Func<Task> Act)[]
        {
            ("unpinned certificate, left open", async () =>
            {
                var connection = await DialAsync(harness, stranger, deadline.Token);
                await Task.Delay(100, deadline.Token);
                await connection.DisposeAsync();
            }),
            ("no client certificate", async () =>
            {
                var connection = await DialAsync(harness, certificate: null, deadline.Token);
                await Task.Delay(100, deadline.Token);
                await connection.DisposeAsync();
            }),
            ("wrong application protocol", () =>
                DialAsync(harness, stranger, deadline.Token, new SslApplicationProtocol("not-promin")).AsTask()),
            ("closed with an arbitrary error code the moment it connects", async () =>
            {
                var connection = await DialAsync(harness, stranger, deadline.Token);
                await connection.CloseAsync(0x3FFF_FFFF_FFFFL, deadline.Token);
                await connection.DisposeAsync();
            }),
            ("disposed the moment it connects", async () =>
            {
                var connection = await DialAsync(harness, stranger, deadline.Token);
                await connection.DisposeAsync();
            }),
            ("handshake cancelled part-way", async () =>
            {
                using var soon = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                soon.CancelAfter(TimeSpan.FromMilliseconds(1));
                var connection = await DialAsync(harness, stranger, soon.Token);
                await connection.DisposeAsync();
            }),
        };

        foreach (var (name, act) in misbehaviours)
        {
            for (var repeat = 0; repeat < 3; repeat++)
            {
                var clientSide = await Record.ExceptionAsync(act);
                if (repeat == 0)
                {
                    output.WriteLine($"peer: {name} -> client saw {Describe(clientSide)}");
                }
            }
        }

        // Give the listener's side of those handshakes time to fail and be reported.
        await Task.Delay(TimeSpan.FromSeconds(4), deadline.Token);
        foreach (var kind in seen.Select(Describe).GroupBy(text => text).OrderByDescending(group => group.Count()))
        {
            output.WriteLine($"accept reported x{kind.Count()}: {kind.Key}");
        }

        Assert.NotEmpty(seen);

        // The listener is still serving.
        var (master, agent) = await harness.ConnectAsync(deadline.Token);
        var (masterControl, agentPending) =
            await QuicLoopbackHarness.OpenControlPairAsync(master, agent, deadline.Token);
        await using var ownedMasterControl = masterControl;
        await masterControl.SendAsync(new StartBroadcast(1920, 1080, 15), deadline.Token);
        await using var agentControl = await agentPending;
        Assert.Equal(new StartBroadcast(1920, 1080, 15), await agentControl.ReceiveAsync(deadline.Token));
    }

    /// <summary>
    /// The rejection callback is the pairing UI's. If it throws, that is the UI's bug and must
    /// not take the listener down with it. Fails an accept loop that invokes the callback
    /// unguarded — there, the callback's exception comes out of AcceptAsync and the agent stops
    /// accepting anyone.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task AcceptLoop_Survives_ARejectionCallbackThatThrows()
    {
        using var deadline = QuicLoopbackHarness.Deadline(TestContext.Current.CancellationToken);
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

    // ---- (c) A flood of refused dials does not delay a legitimate master ------------------

    /// <summary>
    /// Fifty refused dials, all of them queued at the listener at once, and then the paired
    /// master. Each refusal is one real handshake the listener has already done the work for;
    /// the loop consumes them as fast as they come out, so the master is accepted within a
    /// second of dialling. Fails a loop that pauses between failures: with the previous backoff
    /// (up to 500 ms a failure) the master waited more than twenty seconds behind them.
    /// </summary>
    [Fact(Timeout = 120000)]
    public async Task AFloodOfRefusedDials_DoesNotDelayALegitimateMaster()
    {
        const int refusedDials = 50;
        using var deadline = QuicLoopbackHarness.Deadline(TestContext.Current.CancellationToken, 90);
        await using var harness = await QuicLoopbackHarness.StartAsync(deadline.Token);
        using var stranger = SelfSignedCertificateFactory.Create("master-stranger");

        var flooding = Stopwatch.StartNew();
        var dials = await Task.WhenAll(Enumerable.Range(0, refusedDials).Select(async _ =>
            await Record.ExceptionAsync(async () => await harness.ConnectRawAsync(deadline.Token, stranger))));
        output.WriteLine(
            $"{refusedDials} dials issued in {flooding.ElapsedMilliseconds} ms " +
            $"({dials.Count(failure => failure is null)} completed their own side of the handshake)");

        var acceptTask = harness.Listener.AcceptAsync(deadline.Token);
        var connector = new QuicMasterConnector(harness.MasterCertificate, harness.MasterTrust);

        var dialled = Stopwatch.StartNew();
        await using var master = await connector.ConnectAsync(
            harness.Endpoint, QuicLoopbackHarness.AgentDeviceId, deadline.Token);
        await using var agent = await acceptTask;
        dialled.Stop();

        output.WriteLine($"the master was accepted {dialled.ElapsedMilliseconds} ms after dialling");
        Assert.True(
            dialled.Elapsed < TimeSpan.FromSeconds(1),
            $"the master waited {dialled.ElapsedMilliseconds} ms behind {refusedDials} refused dials");
        Assert.Equal(QuicLoopbackHarness.MasterDeviceId, agent.PinnedDeviceId);

        // And they really were fifty refusals, each one reported.
        for (var poll = 0; poll < 100 && harness.RejectedHandshakes < refusedDials; poll++)
        {
            await Task.Delay(100, deadline.Token);
        }

        output.WriteLine(
            $"{harness.RejectedHandshakes} refusals reported {flooding.ElapsedMilliseconds} ms after the first dial");
        Assert.Equal(refusedDials, harness.RejectedHandshakes);
    }

    /// <summary>
    /// Many strangers one after another, then the real master. Fails a loop that gives up, or
    /// dies, after some number of consecutive rejections — which would let anyone on the LAN
    /// take an agent off the air by dialling it a few times.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task AcceptLoop_KeepsServing_AfterManyConsecutiveRejections()
    {
        using var deadline = QuicLoopbackHarness.Deadline(TestContext.Current.CancellationToken, 40);
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
    // These use constructed exceptions on purpose and claim nothing about what the runtime
    // throws: they pin what the pump does with ANY exception, which is the point of it.

    /// <summary>
    /// An accept that fails instantly and for ever, with no peer behind it, must not spin a
    /// core. Unbounded, this accept source is called about 750,000 times in the second the test
    /// runs; the rate guard holds it to its budget of 200 per 100 ms.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task Pump_DoesNotSpin_WhenAcceptFailsInstantlyForEver()
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
            acceptFailed: _ => Interlocked.Increment(ref reported));

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        stop.CancelAfter(TimeSpan.FromSeconds(1));
        await pump.RunAsync(stop.Token);

        output.WriteLine($"{attempts} accept attempts in one second");
        var perSecond = QuicAcceptPump.MaxFailuresPerInterval
            * (int)(TimeSpan.FromSeconds(1) / QuicAcceptPump.FailureInterval);
        Assert.Equal(2000, perSecond);

        // It does keep trying — a whole budget's worth at least — and it does not run away.
        Assert.InRange(Volatile.Read(ref attempts), QuicAcceptPump.MaxFailuresPerInterval, perSecond * 12 / 10);
        Assert.Equal(Volatile.Read(ref attempts), Volatile.Read(ref reported));
        Assert.Equal(Volatile.Read(ref attempts), pump.ConsecutiveFailures);
    }

    /// <summary>
    /// The guard is a rate, not a delay per failure: a burst smaller than the budget is consumed
    /// with no pause at all. Fails a backoff, however small.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task Pump_ConsumesABurstOfFailures_WithoutPausing()
    {
        const int burst = 150;
        var attempts = 0;
        var parked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = Stopwatch.StartNew();
        var pump = new QuicAcceptPump(
            accept: async ct =>
            {
                if (Interlocked.Increment(ref attempts) <= burst)
                {
                    throw new AuthenticationException("refused");
                }

                started.Stop();
                parked.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);
                throw new InvalidOperationException("unreachable");
            },
            deliver: (_, _) => ValueTask.CompletedTask,
            acceptFailed: null);

        using var stop = new CancellationTokenSource();
        var running = pump.RunAsync(stop.Token);
        await parked.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        output.WriteLine($"{burst} failures consumed in {started.Elapsed.TotalMilliseconds:0.0} ms");
        Assert.True(burst < QuicAcceptPump.MaxFailuresPerInterval);
        Assert.True(
            started.Elapsed < QuicAcceptPump.FailureInterval,
            $"{burst} failures took {started.Elapsed.TotalMilliseconds:0.0} ms: something paused");

        await stop.CancelAsync();
        await running;
    }

    /// <summary>
    /// Nothing that comes out of accept ends the pump — not the exception types that look like
    /// a refused peer, and not the ones that look like a local fault. Only cancellation does.
    /// Fails any version that sorts exceptions into "carry on" and "stop".
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task Pump_NeverStops_WhateverAcceptThrows()
    {
        Exception[] anything =
        [
            new AuthenticationException("bad certificate"),
            new QuicException(QuicError.InternalError, null, "looks local, is peer-inducible"),
            new QuicException(QuicError.CallbackError, null, "x"),
            new QuicException(QuicError.ConnectionAborted, 7, "x"),
            new QuicException(QuicError.OperationAborted, null, "x"),
            new SocketException((int)SocketError.ConnectionReset),
            new InvalidOperationException("x"),
            new ObjectDisposedException("x"),
            new IOException("x"),
        ];

        var thrown = 0;
        var reported = new ConcurrentQueue<Exception>();
        var parked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pump = new QuicAcceptPump(
            accept: async ct =>
            {
                var next = Interlocked.Increment(ref thrown) - 1;
                if (next < anything.Length)
                {
                    throw anything[next];
                }

                parked.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);
                throw new InvalidOperationException("unreachable");
            },
            deliver: (_, _) => ValueTask.CompletedTask,
            acceptFailed: reported.Enqueue);

        using var stop = new CancellationTokenSource();
        var running = pump.RunAsync(stop.Token);

        await parked.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.False(running.IsCompleted, "the pump stopped on one of the exceptions");
        Assert.Equal(anything, reported.ToArray());

        // Cancellation is the only way out, and it is a normal return.
        await stop.CancelAsync();
        await running;
        Assert.True(running.IsCompletedSuccessfully);
    }

    /// <summary>A callback that throws on every failure does not stop the pump either.</summary>
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
            acceptFailed: _ => throw new InvalidOperationException("observer fell over"));

        using var stop = new CancellationTokenSource();
        var running = pump.RunAsync(stop.Token);

        await parked.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.False(running.IsCompleted, "the pump died on a throwing callback");

        await stop.CancelAsync();
        await running;
    }

    /// <summary>Dials the harness's listener the way a peer that is not a paired master might.</summary>
    private static ValueTask<QuicConnection> DialAsync(
        QuicLoopbackHarness harness,
        X509Certificate2? certificate,
        CancellationToken ct,
        SslApplicationProtocol? protocol = null) =>
        QuicConnection.ConnectAsync(
            new QuicClientConnectionOptions
            {
                RemoteEndPoint = harness.Endpoint,
                DefaultStreamErrorCode = 0,
                DefaultCloseErrorCode = 0,
                ClientAuthenticationOptions = new SslClientAuthenticationOptions
                {
                    TargetHost = harness.Endpoint.Address.ToString(),
                    ApplicationProtocols = [protocol ?? QuicTransportSettings.ApplicationProtocol],
                    ClientCertificates = certificate is null ? null : [certificate],
                    RemoteCertificateValidationCallback = (_, _, _, _) => true,
                },
            },
            ct);

    private static string Describe(Exception? failure) => failure switch
    {
        null => "no exception",
        QuicException quic => $"QuicException({quic.QuicError}, app={quic.ApplicationErrorCode}): {quic.Message}",
        _ => $"{failure.GetType().Name}: {failure.Message}",
    };
}
