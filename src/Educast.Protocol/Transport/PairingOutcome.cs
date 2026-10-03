namespace Educast.Protocol.Transport;

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
    private PairingOutcome(bool paired, string? deviceId, string? certificateThumbprint, string? failureReason)
    {
        Paired = paired;
        DeviceId = deviceId;
        CertificateThumbprint = certificateThumbprint;
        FailureReason = failureReason;
    }

    /// <summary>True if, and only if, the mutual handshake succeeded and the peer was pinned.</summary>
    public bool Paired { get; }

    /// <summary>The device id the peer was pinned under. Null when not paired.</summary>
    public string? DeviceId { get; }

    /// <summary>The SHA-256 thumbprint that was pinned. Null when not paired.</summary>
    public string? CertificateThumbprint { get; }

    /// <summary>Why pairing did not happen, in words an operator can be shown. Null when paired.</summary>
    public string? FailureReason { get; }

    internal static PairingOutcome Succeeded(string deviceId, string certificateThumbprint) =>
        new(paired: true, deviceId, certificateThumbprint, failureReason: null);

    internal static PairingOutcome Failed(string reason) =>
        new(paired: false, deviceId: null, certificateThumbprint: null, reason);

    public override string ToString() =>
        Paired ? $"Paired with {DeviceId}" : $"Not paired: {FailureReason}";
}

/// <summary>
/// A pin was refused because it would have replaced, or duplicated, one that already exists.
/// </summary>
public sealed class PinConflictException : InvalidOperationException
{
    public PinConflictException(string message) : base(message) { }
}
