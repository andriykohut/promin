namespace Educast.Protocol.Transport;

/// <summary>Why a pairing operation did not pair, for code that has to act on it.</summary>
public enum PairingFailure
{
    /// <summary>It paired.</summary>
    None = 0,

    /// <summary>
    /// Master side: nothing answered at the address. The agent PC is off, off the network, or
    /// not running Educast. The failure an operator will meet most.
    /// </summary>
    AgentUnreachable,

    /// <summary>
    /// The peer refused this side's connection. On the master this almost always means pairing
    /// is not open on that PC, or its pairing window has already been used.
    /// </summary>
    RefusedByPeer,

    /// <summary>
    /// This side refused the certificate the peer presented: it names a different device than
    /// the one being paired, or it would replace an existing pin, or it already belongs to
    /// another device. The reason says which, and only this failure can be cured by unpinning.
    /// </summary>
    PeerCertificateRefused,

    /// <summary>
    /// The peer did not prove the pre-shared key. The keys entered on the two machines differ,
    /// or something between them is intercepting the connection.
    /// </summary>
    KeyNotProven,

    /// <summary>The peer was reached, or let in, but the exchange was not finished in time.</summary>
    TimedOut,

    /// <summary>The connection was lost, or the peer sent something unintelligible, mid-pairing.</summary>
    ConnectionLost,

    /// <summary>
    /// The peer proved the key but the pairing was not completed on its side: on the master,
    /// the agent never said it had recorded the pairing; on the agent, the master reported that
    /// it had not. This side has recorded nothing. Pair again; it is safe to repeat.
    /// </summary>
    NotConfirmed,

    /// <summary>Agent side: no master dialled in before the pairing window closed.</summary>
    NoMasterArrived,

    /// <summary>
    /// Agent side: the pairing window was used up by a machine whose certificate was refused.
    /// A window admits one certificate; a new one has to be opened.
    /// </summary>
    WindowSpent,
}

/// <summary>
/// The result of one pairing operation — and the only thing in the transport that means "this
/// peer is now paired".
/// </summary>
/// <remarks>
/// A connected <see cref="QuicChannel"/> does not mean that. TLS 1.3 validates the client
/// certificate after the client's own handshake has completed, so a master that an agent
/// refuses still gets a successful connect and a channel object; it only learns it was refused
/// when it uses the connection. Code that needs to know whether pairing happened asks this
/// type, never the existence of a channel.
///
/// When <see cref="Paired"/> is true the peer's certificate has already been pinned under
/// <see cref="DeviceId"/> by the operation itself, after — and only after — the mutual
/// pre-shared-key handshake succeeded. When it is false nothing was pinned and the store is
/// exactly as it was. The connection the operation used is closed either way: connect normally
/// afterwards, and the pin is what lets that succeed.
/// </remarks>
public sealed class PairingOutcome
{
    private PairingOutcome(
        PairingFailure failure, string? deviceId, string? certificateThumbprint, string? failureReason)
    {
        Failure = failure;
        DeviceId = deviceId;
        CertificateThumbprint = certificateThumbprint;
        FailureReason = failureReason;
    }

    /// <summary>True if, and only if, the mutual handshake succeeded and the peer was pinned.</summary>
    public bool Paired => Failure == PairingFailure.None;

    /// <summary>What went wrong, as a value. <see cref="PairingFailure.None"/> when paired.</summary>
    public PairingFailure Failure { get; }

    /// <summary>The device id the peer was pinned under. Null when not paired.</summary>
    public string? DeviceId { get; }

    /// <summary>The SHA-256 thumbprint that was pinned. Null when not paired.</summary>
    public string? CertificateThumbprint { get; }

    /// <summary>
    /// What went wrong, in words an operator can be shown. It states what is known and what the
    /// likely causes are; it does not advise a remedy unless that remedy is known to apply.
    /// Null when paired.
    /// </summary>
    public string? FailureReason { get; }

    internal static PairingOutcome Succeeded(string deviceId, string certificateThumbprint) =>
        new(PairingFailure.None, deviceId, certificateThumbprint, failureReason: null);

    internal static PairingOutcome Failed(PairingFailure failure, string reason)
    {
        ArgumentOutOfRangeException.ThrowIfEqual((int)failure, (int)PairingFailure.None);
        return new PairingOutcome(failure, deviceId: null, certificateThumbprint: null, reason);
    }

    public override string ToString() =>
        Paired ? $"Paired with {DeviceId}" : $"Not paired ({Failure}): {FailureReason}";
}

/// <summary>
/// A pin was refused because it would have replaced, or duplicated, one that already exists.
/// </summary>
public sealed class PinConflictException : InvalidOperationException
{
    public PinConflictException(string message) : base(message) { }
}

/// <summary>What a pairing operation treats as its own fault rather than the peer's.</summary>
internal static class PairingFaults
{
    /// <summary>
    /// True for exceptions that mean this process did something wrong — used a disposed object,
    /// passed a bad argument, ran on a platform without QUIC, aborted its own operation. Those
    /// escape a pairing operation. Everything else the peer or the network can cause, and comes
    /// back as a <see cref="PairingOutcome"/>.
    /// </summary>
    public static bool IsProgrammingError(Exception failure) => failure
        is ArgumentException
        or NullReferenceException
        or NotImplementedException
        or PlatformNotSupportedException
        or System.Net.Quic.QuicException { QuicError: System.Net.Quic.QuicError.OperationAborted }
        || (failure is InvalidOperationException && failure is not PinConflictException);
}
