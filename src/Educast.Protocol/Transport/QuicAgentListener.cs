using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Runtime.CompilerServices;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Channels;
using Educast.Protocol.Messages;

namespace Educast.Protocol.Transport;

/// <summary>
/// The agent (client machine) side of the transport: listens for the master to dial in and
/// hands back one <see cref="QuicChannel"/> per accepted connection, or pairs with a master.
/// </summary>
/// <remarks>
/// A master dials in one of two ways, and says which with the TLS server name it sends
/// (<see cref="QuicTransportSettings.PairingServerName"/> to pair, none otherwise). The two
/// kinds of connection are judged by different rules from the first packet and never meet:
///
/// <b>An ordinary connection</b> is judged by the pins alone. Masters are pinned by their real
/// device id, like every other peer; the dialling master is unknown until TLS presents its
/// certificate, so the thumbprint is resolved back to a device id through
/// <see cref="PeerCertificateStore.TryGetDeviceIdForThumbprint"/>. A certificate that resolves
/// to no pin is refused during the handshake. A pairing window being open makes no difference
/// to it. These connections come out of <see cref="AcceptAsync"/>.
///
/// <b>A pairing connection</b> is judged by the open pairing window alone — refused outright if
/// there is none — is granted no video credit at all, and goes to
/// <see cref="AcceptPairingAsync"/> and nowhere else. That is the only way a master comes to be
/// pinned: the operation runs the mutual room-code handshake with whoever the window
/// admitted and pins that certificate if and only if the handshake succeeded.
///
/// Connections are taken off the QUIC listener by a single background
/// <see cref="QuicAcceptPump"/>, started by <see cref="StartAsync"/>. Nothing a remote peer
/// does stops it, and it never waits on the application: a pairing connection is claimed
/// whether or not anyone is calling <see cref="AcceptAsync"/>.
/// </remarks>
public sealed class QuicAgentListener : IAsyncDisposable
{
    /// <summary>
    /// Established ordinary connections waiting for <see cref="AcceptAsync"/>. When the
    /// application is not accepting and this fills, further ordinary connections are closed
    /// rather than queued — the accept loop must never block here, because the same loop is
    /// what delivers pairing connections.
    /// </summary>
    internal const int AcceptQueueCapacity = 16;

    private static readonly object PairingMarker = new();

    private readonly X509Certificate2 _localCertificate;
    private readonly PeerCertificateStore _trustedMasters;
    private readonly int _port;
    private readonly IPAddress _bindAddress;
    private readonly Action<Exception>? _connectionRejected;
    private readonly int _inboundFrameQueueCapacity;
    private readonly Channel<QuicChannel> _established = Channel.CreateBounded<QuicChannel>(
        new BoundedChannelOptions(AcceptQueueCapacity) { FullMode = BoundedChannelFullMode.Wait });

    /// <summary>Which inbound connections were dialled as pairing connections.</summary>
    private readonly ConditionalWeakTable<QuicConnection, object> _pairingConnections = new();
    private readonly CancellationTokenSource _pumpCts = new();
    private readonly CancellationToken _shutdown;
    private QuicListener? _listener;
    private QuicAcceptPump? _pump;
    private Task? _pumpTask;
    private PairingTrustWindow? _pairingWindow;
    private SelfProbe? _selfProbe;
    private int _disposed;

    public QuicAgentListener(
        X509Certificate2 localCertificate,
        PeerCertificateStore trustedMasters,
        int port,
        IPAddress? bindAddress = null,
        Action<Exception>? connectionRejected = null,
        int inboundFrameQueueCapacity = QuicChannel.DefaultInboundFrameQueueCapacity)
    {
        ArgumentNullException.ThrowIfNull(localCertificate);
        ArgumentNullException.ThrowIfNull(trustedMasters);
        ArgumentOutOfRangeException.ThrowIfNegative(port);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 65535);
        ArgumentOutOfRangeException.ThrowIfLessThan(inboundFrameQueueCapacity, 1);

        _localCertificate = localCertificate;
        _trustedMasters = trustedMasters;
        _port = port;
        _bindAddress = bindAddress ?? IPAddress.Any;
        _connectionRejected = connectionRejected;
        _inboundFrameQueueCapacity = inboundFrameQueueCapacity;
        _shutdown = _pumpCts.Token;
    }

    /// <summary>The endpoint actually bound, which resolves port 0 to the port the OS chose.</summary>
    public IPEndPoint LocalEndPoint =>
        _listener?.LocalEndPoint ?? throw new InvalidOperationException("The listener has not been started.");

    /// <summary>
    /// Accept failures since the last connection that was accepted. Zero on a healthy listener;
    /// a number that keeps climbing is a machine being dialled by something it does not trust,
    /// and is what a diagnostics view would show.
    /// </summary>
    public int ConsecutiveAcceptFailures => _pump?.ConsecutiveFailures ?? 0;

    /// <summary>Ordinary connections established and not yet taken by <see cref="AcceptAsync"/>.</summary>
    internal int PendingAccepts => _established.Reader.Count;

    /// <summary>
    /// Binds the port, starts accepting, and proves that this machine's own certificate can
    /// actually complete a TLS handshake before returning.
    /// </summary>
    /// <remarks>
    /// The proof is a handshake against itself over loopback. It exists because a credential
    /// that will not load does not fail here by itself: the TLS stack loads it per inbound
    /// connection, and the failure then comes out of the accept loop as an
    /// <c>AuthenticationException</c> — indistinguishable from a refused stranger. Without the
    /// proof, an agent with a broken certificate would start "successfully" and then turn every
    /// master away for ever. With it, that agent fails to start, and says why.
    /// </remarks>
    /// <exception cref="LocalCredentialException">
    /// The certificate could not be used for a handshake. The listener has been shut down and
    /// cannot be restarted; fix the certificate and create a new one.
    /// </exception>
    public async Task StartAsync(CancellationToken ct)
    {
        QuicTransportSettings.EnsureSupported();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        if (_listener is not null)
        {
            throw new InvalidOperationException("The listener has already been started.");
        }

        var options = new QuicListenerOptions
        {
            ListenEndPoint = new IPEndPoint(_bindAddress, _port),
            ApplicationProtocols = [QuicTransportSettings.ApplicationProtocol],
            ListenBacklog = QuicTransportSettings.ListenBacklog,
            ConnectionOptionsCallback = (connection, hello, _) =>
                ValueTask.FromResult(ChooseConnectionOptions(connection, hello)),
        };

        var listener = await QuicListener.ListenAsync(options, ct).ConfigureAwait(false);
        _listener = listener;
        _pump = new QuicAcceptPump(listener.AcceptConnectionAsync, DeliverAsync, OnAcceptFailed);
        _pumpTask = _pump.RunAsync(_shutdown);

        try
        {
            await ProveOwnCredentialAsync(listener, ct).ConfigureAwait(false);
        }
        catch
        {
            // A listener that cannot complete a handshake must not be left bound and "running".
            await DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Returns the next connection from a master that is already paired. The channel's
    /// <see cref="QuicChannel.PinnedDeviceId"/> is always set: it is the device id the master's
    /// certificate is pinned under.
    /// </summary>
    /// <remarks>
    /// A peer whose certificate is refused never appears here: the refusal is reported through
    /// the optional <c>connectionRejected</c> callback and the listener carries on. Neither does
    /// a pairing connection — that belongs to <see cref="AcceptPairingAsync"/>. Nothing a remote
    /// peer does can make this method throw: the only things that come out of it are a
    /// connection, the caller's cancellation, and <see cref="ObjectDisposedException"/> once the
    /// listener has been disposed.
    /// </remarks>
    /// <exception cref="ObjectDisposedException">The listener was disposed.</exception>
    public async Task<QuicChannel> AcceptAsync(CancellationToken ct)
    {
        EnsureStarted();

        try
        {
            return await _established.Reader.ReadAsync(ct).ConfigureAwait(false);
        }
        catch (ChannelClosedException)
        {
            // The queue is completed by disposal and by nothing else.
            throw new ObjectDisposedException(nameof(QuicAgentListener));
        }
    }

    /// <summary>
    /// Opens a pairing window and pairs with the one master that dials in to pair during it:
    /// the mutual room-code handshake, and — on success, and only on success — a pin for
    /// the certificate that master presented, under the device id its certificate carries.
    /// </summary>
    /// <remarks>
    /// <b>It does not throw for anything the master or the network does.</b> Each such failure
    /// comes back as a <see cref="PairingOutcome"/> whose <see cref="PairingOutcome.Failure"/>
    /// says which, with the pins exactly as they were.
    ///
    /// This is the only thing that lets an unpinned certificate through, and only on a
    /// connection dialled as a pairing connection. The permission is <b>scoped to this one
    /// call</b>: it does not exist before it and is gone when it returns. It is
    /// <b>single-use</b>: the first certificate to arrive on a pairing connection consumes it,
    /// whether that master then pairs or not — and if that certificate is refused, the call
    /// returns at once with <see cref="PairingFailure.WindowSpent"/> rather than waiting for a
    /// master it can no longer admit. And it is <b>bounded</b> twice over, whatever token is
    /// passed: the window waits at most <paramref name="window"/> for a master to dial (default
    /// <see cref="QuicTransportSettings.DefaultPairingWindow"/>, at most
    /// <see cref="QuicTransportSettings.MaxPairingWindow"/>), and once one is admitted the
    /// handshake has <paramref name="handshakeTimeout"/> to finish (default five seconds).
    ///
    /// A master name already pinned to a different certificate is not re-keyed; unpin it first.
    /// A master already pinned to the same certificate may pair again, which is how a pairing
    /// that only one side recorded is finished. Masters that are already paired keep connecting
    /// normally through <see cref="AcceptAsync"/> while the window is open, and do not touch it.
    ///
    /// <paramref name="admission"/> says whose dial the window may let in:
    /// <see cref="PairingAdmission.Anyone"/> (the default, and what the window did before it was a parameter)
    /// or <see cref="PairingAdmission.PinnedPeersOnly"/>, which turns any certificate that is not pinned away
    /// during TLS without using the window up.
    ///
    /// <b>Success is said out loud.</b> Having verified the master and pinned it, this side
    /// sends <see cref="PairConfirm"/>; the master pins on that and on nothing else. On every
    /// other way out the control stream is aborted, never merely closed. The connection is
    /// closed either way; the master reconnects normally afterwards.
    /// </remarks>
    /// <exception cref="InvalidOperationException">A pairing window is already open.</exception>
    /// <param name="roomCode">The room code's ASCII bytes.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="window"/> is not greater than zero and at most ten minutes.
    /// </exception>
    public async Task<PairingOutcome> AcceptPairingAsync(
        byte[] roomCode,
        CancellationToken ct,
        TimeSpan? window = null,
        TimeSpan? handshakeTimeout = null,
        PairingAdmission admission = PairingAdmission.Anyone)
    {
        ArgumentNullException.ThrowIfNull(roomCode);

        var lifetime = window ?? QuicTransportSettings.DefaultPairingWindow;
        var handshakeBudget = handshakeTimeout ?? QuicPairingHandshake.DefaultTimeout;
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(lifetime, TimeSpan.Zero, nameof(window));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(lifetime, QuicTransportSettings.MaxPairingWindow, nameof(window));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(handshakeBudget, TimeSpan.Zero, nameof(handshakeTimeout));
        EnsureStarted();

        var trust = new PairingTrustWindow(_trustedMasters, expectedDeviceId: null, lifetime, admission);
        if (Interlocked.CompareExchange(ref _pairingWindow, trust, null) is not null)
        {
            throw new InvalidOperationException("A pairing window is already open on this listener.");
        }

        var confirmed = false;
        QuicChannel? channel = null;
        QuicControlChannel? control = null;
        try
        {
            QuicConnection connection;
            using (var waiting = CancellationTokenSource.CreateLinkedTokenSource(ct, _shutdown))
            {
                waiting.CancelAfter(lifetime);
                try
                {
                    connection = await trust.Claimed.WaitAsync(waiting.Token).ConfigureAwait(false);
                }
                catch (PairingWindowSpentException spent)
                {
                    return PairingOutcome.Failed(
                        PairingFailure.WindowSpent,
                        $"The pairing window was used by a machine that was not accepted: {spent.Message}. " +
                        "A window admits one machine; open pairing again to pair.");
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    return DescribeEmptyWindow(trust);
                }
            }

            channel = new QuicChannel(connection, pinnedDeviceId: null);
            if (channel.RemoteCertificate is not { } masterCertificate
                || channel.RemoteCertificateSubjectName is not { } deviceId
                || channel.RemoteCertificateThumbprint is not { } thumbprint)
            {
                return PairingOutcome.Failed(
                    PairingFailure.PeerCertificateRefused, "The master presented a certificate that names no device.");
            }

            // The certificate that actually arrived on this connection, and this side's own.
            var binding = CertificateBinding.Compute(masterCertificate, _localCertificate);

            using var handshake = CancellationTokenSource.CreateLinkedTokenSource(ct);
            handshake.CancelAfter(handshakeBudget);

            try
            {
                control = await channel.AcceptControlChannelAsync(handshake.Token).ConfigureAwait(false);
            }
            catch (ChannelClosedException)
            {
                return PairingOutcome.Failed(
                    PairingFailure.ConnectionLost, "The master disconnected before starting the pairing handshake.");
            }

            var result = await QuicPairingHandshake
                .RunAsAgentAsync(control, roomCode, binding, handshake.Token, handshakeBudget)
                .ConfigureAwait(false);
            if (result != PairingHandshakeResult.Paired)
            {
                return DescribeHandshake(result);
            }

            // The master has proved the key. Record it, then say so: the master is waiting for
            // exactly that, and will not record anything on its side until it hears it.
            var pinnedBefore = IsPinnedAs(deviceId, thumbprint);
            try
            {
                _trustedMasters.Pin(deviceId, thumbprint);
            }
            catch (PinConflictException conflict)
            {
                return PairingOutcome.Failed(PairingFailure.PeerCertificateRefused, conflict.Message);
            }

            try
            {
                await control.SendAsync(new PairConfirm(), handshake.Token).ConfigureAwait(false);
                confirmed = true;
            }
            finally
            {
                // A pairing this side could not confirm is one the master will not record. Do
                // not keep half of it — unless the pin was already there before this call.
                if (!confirmed && !pinnedBefore)
                {
                    _trustedMasters.Unpin(deviceId);
                }
            }

            if (await MasterDisownedAsync(control, handshake.Token).ConfigureAwait(false))
            {
                // The master said, explicitly, that it did not record the pairing. Keeping this
                // side's half would leave the two machines disagreeing about whether they are
                // paired, so it goes too — unless it was already there before this call.
                if (!pinnedBefore)
                {
                    _trustedMasters.Unpin(deviceId);
                }

                return PairingOutcome.Failed(
                    PairingFailure.NotConfirmed,
                    "The master proved the room code but then reported that it did not record the " +
                    "pairing, so nothing was recorded here either. It is safe to pair again.");
            }

            return PairingOutcome.Succeeded(deviceId, thumbprint);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return PairingOutcome.Failed(
                PairingFailure.TimedOut, "A master was let in but the pairing handshake did not finish in time.");
        }
        catch (Exception failure)
            when (failure is not OperationCanceledException && !PairingFaults.IsProgrammingError(failure))
        {
            return PairingOutcome.Failed(
                PairingFailure.ConnectionLost,
                $"The connection to the master was lost during pairing. ({failure.Message})");
        }
        finally
        {
            trust.Close();
            Interlocked.CompareExchange(ref _pairingWindow, null, trust);

            if (control is not null)
            {
                if (!confirmed)
                {
                    // Never a graceful close after a pairing that did not happen: the master
                    // must not be able to read a timeout or a failure here as an orderly finish.
                    control.Abort(QuicControlChannel.PairingFailedErrorCode);
                }

                await control.DisposeAsync().ConfigureAwait(false);
            }

            if (channel is not null)
            {
                await channel.DisposeAsync().ConfigureAwait(false);
            }
            else if (trust.Claimed.IsCompletedSuccessfully)
            {
                // Claimed in the instant the wait gave up: nobody else will ever dispose it.
                await trust.Claimed.Result.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private PairingOutcome DescribeEmptyWindow(PairingTrustWindow trust)
    {
        if (_shutdown.IsCancellationRequested)
        {
            return PairingOutcome.Failed(
                PairingFailure.NoMasterArrived, "The listener was shut down while the pairing window was open.");
        }

        return trust.HasAdmitted
            ? PairingOutcome.Failed(
                PairingFailure.TimedOut, "A master was let in, but its connection was never completed.")
            : PairingOutcome.Failed(
                PairingFailure.NoMasterArrived, "No master dialled in to pair before the pairing window closed.");
    }

    private static PairingOutcome DescribeHandshake(PairingHandshakeResult result) => result switch
    {
        PairingHandshakeResult.KeyNotProven => PairingOutcome.Failed(
            PairingFailure.KeyNotProven,
            "The master did not prove the room code. Either the codes entered on the two PCs differ, " +
            "or something between them is intercepting the connection."),
        PairingHandshakeResult.TimedOut => PairingOutcome.Failed(
            PairingFailure.TimedOut, "A master was let in but the pairing handshake did not finish in time."),
        PairingHandshakeResult.CorruptFrame => PairingOutcome.Failed(
            PairingFailure.ConnectionLost, "The master sent data that could not be understood."),
        _ => PairingOutcome.Failed(
            PairingFailure.ConnectionLost, "The master closed the connection during pairing."),
    };

    /// <summary>
    /// After confirming, holds the connection until the master has finished with it, so that
    /// the connection is not torn down underneath a confirmation the master has not read yet —
    /// and reports whether the master explicitly disowned the pairing.
    /// </summary>
    /// <remarks>
    /// A master that recorded the pairing closes its stream in good order. A master that did
    /// not — its deadline ran out, its caller cancelled, its own pin was refused — aborts the
    /// stream with <see cref="QuicControlChannel.PairingFailedErrorCode"/>, and only that counts
    /// as disowning. A wait that runs out, or a connection that simply vanishes, says nothing
    /// either way; the pairing stands, and if the master did not in fact record it, pairing
    /// again finishes the job.
    /// </remarks>
    private static async Task<bool> MasterDisownedAsync(QuicControlChannel control, CancellationToken ct)
    {
        try
        {
            await control.ReceiveAsync(ct).ConfigureAwait(false);
            return false;
        }
        catch (QuicException aborted) when (
            aborted.QuicError == QuicError.StreamAborted
            && aborted.ApplicationErrorCode == QuicControlChannel.PairingFailedErrorCode)
        {
            return true;
        }
        catch (Exception finished) when (!PairingFaults.IsProgrammingError(finished))
        {
            // Closed in good order, gone, or out of time: nothing more to wait for.
            return false;
        }
    }

    private void EnsureStarted()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (_listener is null)
        {
            throw new InvalidOperationException("The listener has not been started.");
        }
    }

    /// <summary>
    /// Completes one TLS handshake against this listener from this process. The client side
    /// uses a throwaway certificate, so what is being proved is exactly the thing the agent
    /// depends on: that <i>this</i> certificate works as a server credential.
    /// </summary>
    private async Task ProveOwnCredentialAsync(QuicListener listener, CancellationToken ct)
    {
        using var probeCertificate = SelfSignedCertificateFactory.Create("educast-self-probe");
        var probe = new SelfProbe(PeerCertificateStore.ComputeThumbprint(probeCertificate));
        var localThumbprint = PeerCertificateStore.ComputeThumbprint(_localCertificate);
        var target = new IPEndPoint(LoopbackFor(listener.LocalEndPoint.Address), listener.LocalEndPoint.Port);

        Volatile.Write(ref _selfProbe, probe);
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(QuicTransportSettings.HandshakeTimeout);

            Exception clientFailure;
            QuicConnection? client = null;
            try
            {
                client = await QuicConnection.ConnectAsync(
                    new QuicClientConnectionOptions
                    {
                        RemoteEndPoint = target,
                        DefaultStreamErrorCode = 0,
                        DefaultCloseErrorCode = 0,
                        HandshakeTimeout = QuicTransportSettings.HandshakeTimeout,
                        MaxInboundBidirectionalStreams = 0,
                        MaxInboundUnidirectionalStreams = 0,
                        ClientAuthenticationOptions = new SslClientAuthenticationOptions
                        {
                            TargetHost = target.Address.ToString(),
                            ApplicationProtocols = [QuicTransportSettings.ApplicationProtocol],
                            ClientCertificates = [probeCertificate],

                            // It has to be this listener that answered, with this certificate.
                            RemoteCertificateValidationCallback = (_, certificate, _, _) =>
                                certificate is not null
                                && string.Equals(
                                    PeerCertificateStore.ComputeThumbprint(certificate),
                                    localThumbprint,
                                    StringComparison.OrdinalIgnoreCase),
                        },
                    },
                    deadline.Token).ConfigureAwait(false);

                // The client's handshake completing already means the server presented its
                // certificate and proved it holds the key. Waiting for the server's side to
                // finish as well keeps the probe from being reported as a refused stranger.
                await probe.Arrived.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception failure)
            {
                clientFailure = failure;
            }
            finally
            {
                if (client is not null)
                {
                    await client.DisposeAsync().ConfigureAwait(false);
                }
            }

            // The handshake with ourselves failed. If the listener's side saw the attempt at
            // all, its own account of why is the informative one; it can arrive seconds after
            // the client has already been turned away.
            Exception? serverFailure = null;
            if (Volatile.Read(ref probe.OptionsCallbackRuns) > 0)
            {
                try
                {
                    serverFailure = await probe.ServerFailure.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                }
            }

            throw new LocalCredentialException(
                "This machine's own certificate could not complete a TLS handshake, so no master could ever " +
                "connect to it. The listener has been shut down. The inner exception is what the TLS stack " +
                "reported.",
                serverFailure ?? clientFailure);
        }
        finally
        {
            Volatile.Write(ref _selfProbe, null);
        }
    }

    private static IPAddress LoopbackFor(IPAddress bound) =>
        bound.Equals(IPAddress.Any) ? IPAddress.Loopback
        : bound.Equals(IPAddress.IPv6Any) ? IPAddress.IPv6Loopback
        : bound;

    /// <summary>
    /// Every failure out of accept comes through here. While the self-probe is in flight the
    /// first one is kept as the listener's own account of what went wrong.
    /// </summary>
    private void OnAcceptFailed(Exception failure)
    {
        Volatile.Read(ref _selfProbe)?.ServerFailure.TrySetResult(failure);
        _connectionRejected?.Invoke(failure);
    }

    /// <summary>
    /// Routes one established connection, and never waits to do it. A pairing connection goes
    /// to the open window's operation; if the window is gone by now it goes nowhere and is
    /// closed. An ordinary connection, which TLS only let through because it is pinned, goes to
    /// <see cref="AcceptAsync"/> — or is closed if the application has let that queue fill.
    /// </summary>
    private async ValueTask DeliverAsync(QuicConnection connection, CancellationToken ct)
    {
        var thumbprint = connection.RemoteCertificate is { } certificate
            ? PeerCertificateStore.ComputeThumbprint(certificate)
            : null;

        if (_pairingConnections.Remove(connection))
        {
            if (thumbprint is null || Volatile.Read(ref _pairingWindow)?.TryClaim(connection, thumbprint) != true)
            {
                await connection.DisposeAsync().ConfigureAwait(false);
            }

            return;
        }

        if (thumbprint is not null
            && Volatile.Read(ref _selfProbe) is { } probe
            && string.Equals(thumbprint, probe.ClientThumbprint, StringComparison.OrdinalIgnoreCase))
        {
            // This listener's own start-up handshake with itself. It has done its job.
            probe.Arrived.TrySetResult();
            await connection.DisposeAsync().ConfigureAwait(false);
            return;
        }

        if (thumbprint is null || !_trustedMasters.TryGetDeviceIdForThumbprint(thumbprint, out var deviceId))
        {
            // Unreachable while the TLS callback does its job; closed rather than trusted if not.
            await connection.DisposeAsync().ConfigureAwait(false);
            return;
        }

        var channel = new QuicChannel(connection, deviceId, _inboundFrameQueueCapacity);
        if (!_established.Writer.TryWrite(channel))
        {
            // Nobody is accepting and the queue is full. Waiting here would stall the one loop
            // that also delivers pairing connections, so this connection is turned away instead;
            // a paired master simply reconnects.
            await channel.DisposeAsync().ConfigureAwait(false);
            try
            {
                _connectionRejected?.Invoke(new InvalidOperationException(
                    $"A connection from '{deviceId}' was closed because {AcceptQueueCapacity} accepted " +
                    "connections are already waiting and nothing is calling AcceptAsync."));
            }
            catch (Exception)
            {
                // The observer's own failure is not allowed to become the accept loop's.
            }
        }
    }

    /// <summary>
    /// Runs before the handshake, with nothing but the client's hello to go on. The server name
    /// in it selects which rules will judge the certificate that follows, and — because QUIC
    /// stream credit is fixed here, before any certificate has been seen — how much credit the
    /// connection gets.
    /// </summary>
    private QuicServerConnectionOptions ChooseConnectionOptions(QuicConnection connection, SslClientHelloInfo hello)
    {
        if (Volatile.Read(ref _selfProbe) is { } probe)
        {
            Interlocked.Increment(ref probe.OptionsCallbackRuns);
        }

        var pairing = string.Equals(
            hello.ServerName, QuicTransportSettings.PairingServerName, StringComparison.OrdinalIgnoreCase);
        if (pairing)
        {
            _pairingConnections.AddOrUpdate(connection, PairingMarker);
        }

        return new QuicServerConnectionOptions
        {
            DefaultStreamErrorCode = 0,
            DefaultCloseErrorCode = 0,

            // Liveness is QUIC's job: there is no heartbeat message in this protocol.
            IdleTimeout = QuicTransportSettings.IdleTimeout,
            KeepAliveInterval = QuicTransportSettings.KeepAliveInterval,
            HandshakeTimeout = QuicTransportSettings.HandshakeTimeout,

            // An ordinary connection carries video: one unidirectional stream per frame, so the
            // agent must allow a healthy number of them at once, plus one bidirectional stream
            // for control and room for a reconnect's overlap. A pairing connection carries the
            // handshake and nothing else, from a peer that has proved nothing yet: one control
            // stream, and no video credit at all. It is closed when pairing ends, so it never
            // needs any.
            MaxInboundBidirectionalStreams = pairing ? 1 : QuicTransportSettings.MaxInboundBidirectionalStreams,
            MaxInboundUnidirectionalStreams = pairing ? 0 : QuicTransportSettings.AgentMaxInboundUnidirectionalStreams,

            ServerAuthenticationOptions = new SslServerAuthenticationOptions
            {
                ServerCertificate = _localCertificate,
                ApplicationProtocols = [QuicTransportSettings.ApplicationProtocol],
                ClientCertificateRequired = true,
                RemoteCertificateValidationCallback = pairing
                    ? (_, certificate, _, _) => IsAdmittedToPair(certificate)
                    : (_, certificate, _, _) => IsPinnedMaster(certificate),
            },
        };
    }

    /// <summary>An ordinary connection: the pins, and nothing else.</summary>
    private bool IsPinnedMaster(X509Certificate? certificate)
    {
        if (certificate is null)
        {
            return false;
        }

        var thumbprint = PeerCertificateStore.ComputeThumbprint(certificate);
        if (_trustedMasters.TryGetDeviceIdForThumbprint(thumbprint, out _))
        {
            return true;
        }

        // The start-up probe: a throwaway certificate this process generated a moment ago and
        // holds the only key for, accepted only while that probe is in flight.
        return Volatile.Read(ref _selfProbe) is { } probe
            && string.Equals(thumbprint, probe.ClientThumbprint, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A pairing connection: the open pairing window, and nothing else.</summary>
    private bool IsAdmittedToPair(X509Certificate? certificate) =>
        Volatile.Read(ref _pairingWindow)?.TryAdmit(certificate) ?? false;

    private bool IsPinnedAs(string deviceId, string thumbprint) =>
        _trustedMasters.TryGetPinnedThumbprint(deviceId, out var pinned)
        && string.Equals(pinned, thumbprint, StringComparison.OrdinalIgnoreCase);

    /// <summary>The state of the one handshake this listener makes against itself at start-up.</summary>
    private sealed class SelfProbe(string clientThumbprint)
    {
        public int OptionsCallbackRuns;

        public string ClientThumbprint { get; } = clientThumbprint;

        /// <summary>Completes when the probe connection comes out of the accept loop.</summary>
        public TaskCompletionSource Arrived { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>The first failure the accept loop saw while the probe was in flight.</summary>
        public TaskCompletionSource<Exception> ServerFailure { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        // Stop the pump first, so the listener going away under it is a cancellation rather
        // than a failure it reports.
        await _pumpCts.CancelAsync().ConfigureAwait(false);
        Volatile.Read(ref _pairingWindow)?.Close();

        if (_listener is not null)
        {
            await _listener.DisposeAsync().ConfigureAwait(false);
        }

        if (_pumpTask is not null)
        {
            await _pumpTask.ConfigureAwait(false);
        }

        _established.Writer.TryComplete();
        while (_established.Reader.TryRead(out var unclaimed))
        {
            await unclaimed.DisposeAsync().ConfigureAwait(false);
        }

        _pumpCts.Dispose();
    }
}
