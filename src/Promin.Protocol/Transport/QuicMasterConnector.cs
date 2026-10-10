using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Promin.Protocol.Messages;

namespace Promin.Protocol.Transport;

/// <summary>
/// The master (teacher machine) side of the transport: dials one agent and returns the
/// <see cref="QuicChannel"/> for it, or pairs with it.
/// </summary>
/// <remarks>
/// Two operations, and they do not overlap:
///
/// <see cref="ConnectAsync"/> is every ordinary connection. The agent's certificate must match
/// the pin held for the exact device id being dialled — a certificate pinned for one agent never
/// authenticates another — and nothing about it ever trusts a certificate on first use.
///
/// <see cref="PairAsync"/> is the only way an agent comes to be pinned. It dials the agent as a
/// pairing connection, lets one certificate through TLS for that one connection, runs the mutual
/// room-code handshake over it, and pins the certificate if and only if the agent
/// explicitly confirmed.
/// </remarks>
public sealed class QuicMasterConnector
{
    private readonly X509Certificate2 _localCertificate;
    private readonly PeerCertificateStore _trustedAgents;

    public QuicMasterConnector(X509Certificate2 localCertificate, PeerCertificateStore trustedAgents)
    {
        ArgumentNullException.ThrowIfNull(localCertificate);
        ArgumentNullException.ThrowIfNull(trustedAgents);

        _localCertificate = localCertificate;
        _trustedAgents = trustedAgents;
    }

    /// <summary>
    /// Connects to an agent that is already paired. Fails during the handshake if the agent's
    /// certificate does not match the pin for <paramref name="deviceId"/>.
    /// </summary>
    /// <remarks>
    /// <b>A returned channel is a completed connection, not proof the agent accepted this
    /// master.</b> TLS 1.3 validates the client certificate after the client's handshake has
    /// completed, so an agent that refuses this master still lets this method succeed; the
    /// refusal only shows when the connection is used. A reconnect loop must count control
    /// messages that came back, not connects that returned.
    /// </remarks>
    public Task<QuicChannel> ConnectAsync(IPEndPoint endpoint, string deviceId, CancellationToken ct) =>
        ConnectCoreAsync(endpoint, deviceId, pairingWindow: null, ct);

    /// <summary>
    /// Pairs with the agent at <paramref name="endpoint"/> as <paramref name="deviceId"/>: one
    /// connection, the mutual room-code handshake, and — once the agent has explicitly
    /// confirmed, and only then — a pin for the certificate the agent presented. The agent must
    /// have a pairing window open (<see cref="QuicAgentListener.AcceptPairingAsync"/>).
    /// </summary>
    /// <remarks>
    /// <b>It does not throw for anything the agent or the network does.</b> An unreachable
    /// address, a refused connection, a wrong key, a dropped link, a timeout: each comes back as
    /// a <see cref="PairingOutcome"/> whose <see cref="PairingOutcome.Failure"/> says which and
    /// whose pins are exactly as they were. What can come out as an exception is the caller's
    /// own cancellation and programming errors — a bad argument, a disposed object.
    ///
    /// <b>The agent's certificate must name the device being paired</b>, and it is checked
    /// before any handshake message is sent. A device already pinned to a different certificate
    /// is refused rather than re-keyed; unpin it first. A device already pinned to this same
    /// certificate may be paired again, which is how a pairing that only one side recorded is
    /// finished.
    ///
    /// <b>Success is something the agent says.</b> The master pins on the agent's
    /// <see cref="PairConfirm"/> and on nothing else. The stream closing, the wait running out
    /// and the connection going away are never read as acceptance.
    ///
    /// The whole operation runs under its own deadline (<paramref name="timeout"/>, default
    /// <see cref="QuicTransportSettings.PairingTimeout"/>) whatever token is passed, and the
    /// permission to accept an unpinned certificate lives exactly as long as this call. The
    /// connection is closed either way; call <see cref="ConnectAsync"/> afterwards.
    /// </remarks>
    public async Task<PairingOutcome> PairAsync(
        IPEndPoint endpoint,
        string deviceId,
        byte[] roomCode,
        CancellationToken ct,
        TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        ArgumentNullException.ThrowIfNull(roomCode);

        var budget = timeout ?? QuicTransportSettings.PairingTimeout;
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(budget, TimeSpan.Zero);

        var window = new PairingTrustWindow(_trustedAgents, deviceId, budget);
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        operation.CancelAfter(budget);

        var phase = PairingPhase.Reaching;
        var paired = false;
        QuicChannel? channel = null;
        QuicControlChannel? control = null;
        try
        {
            channel = await ConnectCoreAsync(endpoint, deviceId, window, operation.Token).ConfigureAwait(false);
            phase = PairingPhase.Proving;

            if (channel.RemoteCertificate is not { } agentCertificate
                || channel.RemoteCertificateThumbprint is not { } thumbprint)
            {
                return PairingOutcome.Failed(
                    PairingFailure.PeerCertificateRefused, "The agent presented no certificate.");
            }

            // This side's own certificate, and the one that actually arrived on this connection.
            var binding = CertificateBinding.Compute(_localCertificate, agentCertificate);

            control = await channel.OpenControlChannelAsync(operation.Token).ConfigureAwait(false);
            var handshake = await QuicPairingHandshake
                .RunAsMasterAsync(control, roomCode, binding, operation.Token)
                .ConfigureAwait(false);
            if (handshake != PairingHandshakeResult.Paired)
            {
                return DescribeHandshake(handshake);
            }

            // The agent has proved the key and has this side's proof. It has not yet said that
            // it recorded the pairing, and until it does this side records nothing either.
            phase = PairingPhase.AwaitingConfirmation;
            var reply = await control.ReceiveAsync(operation.Token).ConfigureAwait(false);
            if (reply is not PairConfirm)
            {
                return PairingOutcome.Failed(
                    PairingFailure.NotConfirmed,
                    "The agent proved the room code but declined to record the pairing.");
            }

            try
            {
                _trustedAgents.Pin(deviceId, thumbprint);
            }
            catch (PinConflictException conflict)
            {
                return PairingOutcome.Failed(PairingFailure.PeerCertificateRefused, conflict.Message);
            }

            paired = true;
            return PairingOutcome.Succeeded(deviceId, thumbprint);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return DescribeTimeout(phase, endpoint, budget);
        }
        catch (Exception failure)
            when (failure is not OperationCanceledException && !PairingFaults.IsProgrammingError(failure))
        {
            return DescribeFailure(phase, failure, window, endpoint);
        }
        finally
        {
            window.Close();

            if (control is not null)
            {
                if (!paired)
                {
                    // Never a graceful close after a pairing that did not happen.
                    control.Abort(QuicControlChannel.PairingFailedErrorCode);
                }

                await control.DisposeAsync().ConfigureAwait(false);
            }

            if (channel is not null)
            {
                await channel.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private enum PairingPhase
    {
        Reaching,
        Proving,
        AwaitingConfirmation,
    }

    private const string AgentRefusedConnection =
        "The agent refused the connection. Either pairing is not open on that PC, or its pairing window " +
        "has already been used — a window admits one machine. Open pairing on the PC and try again.";

    private static PairingOutcome DescribeHandshake(PairingHandshakeResult handshake) => handshake switch
    {
        PairingHandshakeResult.KeyNotProven => PairingOutcome.Failed(
            PairingFailure.KeyNotProven,
            "The agent did not prove the room code. Either the codes entered on the two PCs differ, " +
            "or something between them is intercepting the connection."),
        PairingHandshakeResult.PeerRefused => PairingOutcome.Failed(
            PairingFailure.RefusedByPeer, AgentRefusedConnection),
        PairingHandshakeResult.TimedOut => PairingOutcome.Failed(
            PairingFailure.TimedOut, "The agent accepted the connection but did not answer in time."),
        PairingHandshakeResult.CorruptFrame => PairingOutcome.Failed(
            PairingFailure.ConnectionLost, "The agent sent data that could not be understood."),
        _ => PairingOutcome.Failed(
            PairingFailure.ConnectionLost, "The agent closed the connection during pairing."),
    };

    private static PairingOutcome DescribeTimeout(PairingPhase phase, IPEndPoint endpoint, TimeSpan budget) =>
        phase switch
        {
            PairingPhase.Reaching => PairingOutcome.Failed(
                PairingFailure.AgentUnreachable,
                $"Nothing at {endpoint} answered within {budget.TotalSeconds:0.#} seconds. Check that the PC " +
                "is switched on, on this network, and running Promin."),
            PairingPhase.AwaitingConfirmation => PairingOutcome.Failed(
                PairingFailure.NotConfirmed,
                "The agent proved the room code but did not confirm the pairing in time. It is safe " +
                "to pair again."),
            _ => PairingOutcome.Failed(
                PairingFailure.TimedOut,
                $"Pairing did not complete within {budget.TotalSeconds:0.#} seconds."),
        };

    private static PairingOutcome DescribeFailure(
        PairingPhase phase, Exception failure, PairingTrustWindow window, IPEndPoint endpoint)
    {
        // This side's own verdict on the agent's certificate comes first: it is the one case in
        // which the reason is known exactly, and the only one where unpinning is a remedy.
        if (window.RefusalReason is { } refusal)
        {
            return PairingOutcome.Failed(
                PairingFailure.PeerCertificateRefused, $"The PC at {endpoint} was not accepted: {refusal}.");
        }

        if (failure is AuthenticationException)
        {
            // A TLS alert from the other end. Nothing in it says more than "refused".
            return PairingOutcome.Failed(PairingFailure.RefusedByPeer, AgentRefusedConnection);
        }

        return phase switch
        {
            PairingPhase.Reaching => PairingOutcome.Failed(
                PairingFailure.AgentUnreachable,
                $"Could not reach an agent at {endpoint}. Check that the PC is switched on, on this network, " +
                $"and running Promin. ({failure.Message})"),
            PairingPhase.AwaitingConfirmation => PairingOutcome.Failed(
                PairingFailure.NotConfirmed,
                "The agent proved the room code but the connection ended before it confirmed the " +
                "pairing. It is safe to pair again."),
            _ => PairingOutcome.Failed(
                PairingFailure.ConnectionLost,
                $"The connection to the agent was lost during pairing. ({failure.Message})"),
        };
    }

    private async Task<QuicChannel> ConnectCoreAsync(
        IPEndPoint endpoint, string deviceId, PairingTrustWindow? pairingWindow, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        QuicTransportSettings.EnsureSupported();

        var options = new QuicClientConnectionOptions
        {
            RemoteEndPoint = endpoint,
            DefaultStreamErrorCode = 0,
            DefaultCloseErrorCode = 0,

            // Liveness is QUIC's job: there is no heartbeat message in this protocol. QUIC
            // negotiates the smaller of the two peers' idle timeouts, so both sides set the same
            // value deliberately rather than inheriting a default.
            IdleTimeout = QuicTransportSettings.IdleTimeout,
            KeepAliveInterval = QuicTransportSettings.KeepAliveInterval,
            HandshakeTimeout = QuicTransportSettings.HandshakeTimeout,

            // The master opens the control stream itself and receives no video, so it grants the
            // agent no stream credit of either kind: agent machines are student-controlled, and every
            // stream of credit is memory a student's PC can make the teacher's laptop allocate.
            MaxInboundBidirectionalStreams = QuicTransportSettings.MasterMaxInboundBidirectionalStreams,
            MaxInboundUnidirectionalStreams = QuicTransportSettings.MasterMaxInboundUnidirectionalStreams,

            ClientAuthenticationOptions = new SslClientAuthenticationOptions
            {
                // The server name is how the agent knows, before the handshake, whether this is
                // a pairing connection or an ordinary one. An ordinary connection sends none.
                TargetHost = pairingWindow is null
                    ? endpoint.Address.ToString()
                    : QuicTransportSettings.PairingServerName,
                ApplicationProtocols = [QuicTransportSettings.ApplicationProtocol],
                ClientCertificates = [_localCertificate],

                // Ordinary: the pin for the dialled device, and nothing else. Pairing: the
                // window, which checks the name, the pins and its own single use.
                RemoteCertificateValidationCallback = (_, certificate, _, _) =>
                    certificate is not null
                    && (pairingWindow is null
                        ? IsPinnedAs(deviceId, certificate)
                        : pairingWindow.TryAdmit(certificate)),
            },
        };

        var connection = await QuicConnection.ConnectAsync(options, ct).ConfigureAwait(false);

        // Judged from the certificate that was actually presented, not echoed from the argument:
        // during a first pairing the device is not pinned yet, and the channel must not say it is.
        var pinnedDeviceId = connection.RemoteCertificate is { } presented && IsPinnedAs(deviceId, presented)
            ? deviceId
            : null;
        return new QuicChannel(connection, pinnedDeviceId, acceptsStreams: false);
    }

    private bool IsPinnedAs(string deviceId, X509Certificate certificate) =>
        _trustedAgents.TryGetPinnedThumbprint(deviceId, out var pinned)
        && string.Equals(pinned, PeerCertificateStore.ComputeThumbprint(certificate), StringComparison.OrdinalIgnoreCase);
}
