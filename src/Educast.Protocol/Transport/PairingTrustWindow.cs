using System.Diagnostics;
using System.Net.Quic;
using System.Security.Cryptography.X509Certificates;

namespace Educast.Protocol.Transport;

/// <summary>
/// The "first use" of trust-on-first-use, scoped to exactly one pairing operation.
/// </summary>
/// <remarks>
/// A peer that has never been paired has no pin, so its certificate has to be let through TLS
/// once for the pre-shared-key handshake to run over it. That permission used to be a mode of
/// the listener or connector object — on for as long as the object lived, for every connection
/// including ordinary reconnects, with disposal as the only way to turn it off. It is now an
/// object that one pairing operation creates, uses and closes:
///
/// <b>Explicit.</b> Nothing admits an unpinned certificate unless a pairing operation made one
/// of these. Ordinary connects and accepts never do.
///
/// <b>Single-use.</b> It admits at most one certificate, once. The first certificate it is
/// asked about consumes it, whether that certificate is admitted or refused, and whether the
/// pairing that follows succeeds or fails.
///
/// <b>Bounded.</b> It stops admitting when its lifetime runs out, and when the operation that
/// owns it closes it — whichever comes first.
///
/// <b>It never re-keys.</b> It refuses a certificate for a device id that is already pinned to
/// a different certificate, and a certificate that is already pinned for a different device.
/// Re-pairing a machine that was reimaged is an explicit
/// <see cref="PeerCertificateStore.Unpin"/> first.
///
/// Callers consult the pins before the window, so a peer that is already pinned and matches
/// never consumes it.
/// </remarks>
internal sealed class PairingTrustWindow
{
    private readonly Lock _gate = new();
    private readonly PeerCertificateStore _pins;
    private readonly string? _expectedDeviceId;
    private readonly TimeSpan _lifetime;
    private readonly long _openedAt = Stopwatch.GetTimestamp();
    private readonly TaskCompletionSource<QuicConnection> _claimed =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _consumed;
    private bool _closed;
    private string? _admittedThumbprint;

    /// <param name="pins">The store this window must never contradict.</param>
    /// <param name="expectedDeviceId">
    /// The device the operation is pairing with, when it knows — the master always does, because
    /// it dials a specific agent. Null on the agent, which learns who the master is from the
    /// certificate; there the certificate's own subject name is the device id.
    /// </param>
    /// <param name="lifetime">How long the window may admit a certificate.</param>
    public PairingTrustWindow(PeerCertificateStore pins, string? expectedDeviceId, TimeSpan lifetime)
    {
        ArgumentNullException.ThrowIfNull(pins);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(lifetime, TimeSpan.Zero);

        _pins = pins;
        _expectedDeviceId = expectedDeviceId;
        _lifetime = lifetime;
    }

    /// <summary>Completes with the connection whose certificate this window admitted.</summary>
    public Task<QuicConnection> Claimed => _claimed.Task;

    /// <summary>
    /// The TLS decision for a certificate that matches no pin. True at most once in the life of
    /// the window.
    /// </summary>
    public bool TryAdmit(X509Certificate? certificate)
    {
        if (certificate is null)
        {
            return false;
        }

        lock (_gate)
        {
            if (_closed || _consumed || Stopwatch.GetElapsedTime(_openedAt) >= _lifetime)
            {
                return false;
            }

            // Consumed by the first certificate it is asked about, admitted or not.
            _consumed = true;

            var deviceId = _expectedDeviceId ?? CertificateIdentity.ReadDeviceId(certificate);
            if (deviceId is null)
            {
                // Nothing to pin it under.
                return false;
            }

            if (_pins.TryGetPinnedThumbprint(deviceId, out _))
            {
                // Already pinned — and to a different certificate, or the pin check that runs
                // before this would have accepted it. A pairing window does not re-key.
                return false;
            }

            var thumbprint = PeerCertificateStore.ComputeThumbprint(certificate);
            if (_pins.TryGetDeviceIdForThumbprint(thumbprint, out _))
            {
                // This certificate already identifies a different device.
                return false;
            }

            _admittedThumbprint = thumbprint;
            return true;
        }
    }

    /// <summary>
    /// Hands the window the established connection its admitted certificate arrived on. False
    /// if this connection is not that one, or the window has already been claimed or closed —
    /// in which case nobody owns the connection and the caller must dispose it.
    /// </summary>
    public bool TryClaim(QuicConnection connection, string thumbprint)
    {
        ArgumentNullException.ThrowIfNull(connection);

        lock (_gate)
        {
            if (_closed
                || _claimed.Task.IsCompleted
                || !string.Equals(_admittedThumbprint, thumbprint, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return _claimed.TrySetResult(connection);
        }
    }

    /// <summary>Ends the window. Nothing is admitted or claimed afterwards.</summary>
    public void Close()
    {
        lock (_gate)
        {
            _closed = true;
        }
    }
}

/// <summary>Reads the device id a certificate carries.</summary>
internal static class CertificateIdentity
{
    private const string CommonNameOid = "2.5.4.3";
    private const int MaxDeviceIdLength = 256;

    /// <summary>
    /// The certificate's subject common name, which is where
    /// <see cref="SelfSignedCertificateFactory"/> puts the device id. Null if there is none or
    /// it is not usable as one.
    /// </summary>
    /// <remarks>
    /// This value is supplied by the peer and proves nothing. It is only ever used as the label
    /// a certificate is pinned under, and only after the peer has proved the pre-shared key; and
    /// since a pin is never replaced, a peer cannot use a name of its choosing to displace a
    /// device that is already pinned under that name.
    /// </remarks>
    public static string? ReadDeviceId(X509Certificate certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);

        var subject = certificate is X509Certificate2 full
            ? full.SubjectName
            : new X500DistinguishedName(certificate.Subject);

        foreach (var relativeName in subject.EnumerateRelativeDistinguishedNames())
        {
            if (relativeName.HasMultipleElements || relativeName.GetSingleElementType().Value != CommonNameOid)
            {
                continue;
            }

            var commonName = relativeName.GetSingleElementValue();
            return string.IsNullOrWhiteSpace(commonName) || commonName.Length > MaxDeviceIdLength
                ? null
                : commonName;
        }

        return null;
    }
}
