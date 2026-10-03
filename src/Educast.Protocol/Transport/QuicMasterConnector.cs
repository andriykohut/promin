using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Educast.Protocol.Messages;

namespace Educast.Protocol.Transport;

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
/// <see cref="PairAsync"/> is the only way an agent comes to be pinned. It lets one unpinned
/// certificate through TLS for the one connection it makes, runs the mutual pre-shared-key
/// handshake over it, and pins the certificate if and only if that succeeded.
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
    /// connection, the mutual pre-shared-key handshake, and — on success, and only on success —
    /// a pin for the certificate the agent presented. The agent must have a pairing window open
    /// (<see cref="QuicAgentListener.AcceptPairingAsync"/>).
    /// </summary>
    /// <remarks>
    /// The whole operation runs under its own deadline (<paramref name="timeout"/>, default
    /// <see cref="QuicTransportSettings.PairingTimeout"/>) whatever token is passed, and the
    /// permission to accept an unpinned certificate lives exactly as long as this call: it is
    /// created here, used for this one connection, and closed before this method returns. It is
    /// also bound to <paramref name="deviceId"/> — if that device is already pinned to a
    /// different certificate, pairing is refused rather than re-keying it; unpin it first.
    ///
    /// A failure the agent or the network can cause comes back as an outcome with
    /// <see cref="PairingOutcome.Paired"/> false, not as an exception, and leaves the pins
    /// exactly as they were. The connection is closed either way; call
    /// <see cref="ConnectAsync"/> afterwards.
    /// </remarks>
    public async Task<PairingOutcome> PairAsync(
        IPEndPoint endpoint,
        string deviceId,
        byte[] presharedKey,
        CancellationToken ct,
        TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        ArgumentNullException.ThrowIfNull(presharedKey);

        var budget = timeout ?? QuicTransportSettings.PairingTimeout;
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(budget, TimeSpan.Zero);

        var window = new PairingTrustWindow(_trustedAgents, deviceId, budget);
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        operation.CancelAfter(budget);

        QuicChannel? channel = null;
        try
        {
            channel = await ConnectCoreAsync(endpoint, deviceId, window, operation.Token).ConfigureAwait(false);
            if (channel.RemoteCertificate is not { } agentCertificate
                || channel.RemoteCertificateThumbprint is not { } thumbprint)
            {
                return PairingOutcome.Failed("The agent presented no certificate.");
            }

            // This side's own certificate, and the one that actually arrived on this connection.
            var binding = CertificateBinding.Compute(_localCertificate, agentCertificate);

            var control = await channel.OpenControlChannelAsync(operation.Token).ConfigureAwait(false);
            await using (control.ConfigureAwait(false))
            {
                if (!await QuicPairingHandshake.RunAsMasterAsync(control, presharedKey, binding, operation.Token)
                        .ConfigureAwait(false))
                {
                    return PairingOutcome.Failed(
                        "The agent did not prove the pre-shared key. Check that a pairing window is open " +
                        "on it and that the same key was entered on both machines.");
                }

                if (await AgentRefusedAsync(control, operation.Token, ct).ConfigureAwait(false))
                {
                    return PairingOutcome.Failed("The agent verified this master but refused to record the pairing.");
                }

                try
                {
                    _trustedAgents.Pin(deviceId, thumbprint);
                }
                catch (PinConflictException conflict)
                {
                    return PairingOutcome.Failed(conflict.Message);
                }

                return PairingOutcome.Succeeded(deviceId, thumbprint);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return PairingOutcome.Failed($"Pairing did not complete within {budget.TotalSeconds:0.#} seconds.");
        }
        catch (AuthenticationException)
        {
            return PairingOutcome.Failed(
                $"The certificate the agent presented was refused. If '{deviceId}' was re-installed, unpin it " +
                "and pair again.");
        }
        catch (QuicException lost) when (lost.QuicError != QuicError.OperationAborted)
        {
            return PairingOutcome.Failed(
                "The connection to the agent failed. Check that it is reachable and that a pairing window " +
                $"is open on it. ({lost.Message})");
        }
        finally
        {
            window.Close();
            if (channel is not null)
            {
                await channel.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Holds the connection open until the agent has finished with the proof it was just sent,
    /// and reports whether it explicitly refused. The agent closes its side of the control
    /// stream once it has pinned; if it could not pin it says so, or aborts the stream. Without
    /// this wait the master would close the connection underneath a proof the agent had not
    /// read yet.
    /// </summary>
    /// <remarks>
    /// Only an explicit refusal counts as one. A wait that merely runs out, or a connection the
    /// agent closed, does not: by this point the agent has proved the pre-shared key, which is
    /// everything the master's own pin depends on.
    /// </remarks>
    private static async Task<bool> AgentRefusedAsync(
        QuicControlChannel control, CancellationToken operation, CancellationToken caller)
    {
        try
        {
            // Anything the agent says here is a refusal; on success it says nothing and closes.
            await control.ReceiveAsync(operation).ConfigureAwait(false);
            return true;
        }
        catch (ControlChannelClosedException)
        {
            return false;
        }
        catch (OperationCanceledException) when (!caller.IsCancellationRequested)
        {
            // The operation's own deadline, not the caller giving up (which propagates).
            return false;
        }
        catch (ControlMessageFormatException)
        {
            return true;
        }
        catch (QuicException aborted) when (aborted.QuicError == QuicError.StreamAborted)
        {
            return true;
        }
        catch (QuicException gone) when (gone.QuicError != QuicError.OperationAborted)
        {
            return false;
        }
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

            // The master receives control messages, not video. It grants the agent enough
            // bidirectional credit for the control stream plus reconnect overlap, and no
            // unidirectional credit at all: agent machines are student-controlled, and every
            // stream of credit is memory a student's PC can make the teacher's laptop allocate.
            MaxInboundBidirectionalStreams = QuicTransportSettings.MaxInboundBidirectionalStreams,
            MaxInboundUnidirectionalStreams = QuicTransportSettings.MasterMaxInboundUnidirectionalStreams,

            ClientAuthenticationOptions = new SslClientAuthenticationOptions
            {
                TargetHost = endpoint.Address.ToString(),
                ApplicationProtocols = [QuicTransportSettings.ApplicationProtocol],
                ClientCertificates = [_localCertificate],
                RemoteCertificateValidationCallback = (_, certificate, _, _) =>
                    certificate is not null
                    && (IsPinnedAs(deviceId, certificate) || (pairingWindow?.TryAdmit(certificate) ?? false)),
            },
        };

        var connection = await QuicConnection.ConnectAsync(options, ct).ConfigureAwait(false);

        // Judged from the certificate that was actually presented, not echoed from the argument:
        // during pairing the device is not pinned yet, and the channel must not say it is.
        var pinnedDeviceId = connection.RemoteCertificate is { } presented && IsPinnedAs(deviceId, presented)
            ? deviceId
            : null;
        return new QuicChannel(connection, pinnedDeviceId);
    }

    private bool IsPinnedAs(string deviceId, X509Certificate certificate) =>
        _trustedAgents.TryGetPinnedThumbprint(deviceId, out var pinned)
        && string.Equals(pinned, PeerCertificateStore.ComputeThumbprint(certificate), StringComparison.OrdinalIgnoreCase);
}
