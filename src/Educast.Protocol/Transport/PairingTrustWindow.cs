using System.Diagnostics;
using System.Net.Quic;
using System.Security.Cryptography.X509Certificates;

namespace Educast.Protocol.Transport;

/// <summary>
/// The "first use" of trust-on-first-use, scoped to exactly one pairing operation. It is the
/// only thing that judges the certificate on a connection dialled in order to pair.
/// </summary>
/// <remarks>
/// A peer that has never been paired has no pin, so its certificate has to be let through TLS
/// once for the room-code handshake to run over it. That permission is an object that one
/// pairing operation creates, uses and closes:
///
/// <b>Explicit.</b> Nothing admits an unpinned certificate unless a pairing operation made one
/// of these, and only connections dialled as pairing connections are judged by it. Ordinary
/// connects and accepts never consult it.
///
/// <b>Single-use.</b> It admits at most one certificate, once. The first certificate it is
/// asked about consumes it, whether that certificate is admitted or refused, and whether the
/// pairing that follows succeeds or fails.
///
/// <b>Bounded.</b> It stops admitting when its lifetime runs out, and when the operation that
/// owns it closes it — whichever comes first.
///
/// <b>It checks the name.</b> A certificate that names no device has nothing to be pinned
/// under. On the master, which knows which device it dialled, a certificate naming any other
/// device is refused before a single handshake message is exchanged.
///
/// <b>It never re-keys.</b> A device id that is already pinned to a <i>different</i>
/// certificate is refused, and so is a certificate already pinned for a different device.
/// Re-pairing a machine that was reimaged is an explicit <see cref="PeerCertificateStore.Unpin"/>
/// first.
///
/// <b>Anyone, or pinned peers only.</b> A window opened for <see cref="PairingAdmission.PinnedPeersOnly"/>
/// refuses a certificate that is not pinned, and that refusal does not consume the window: the peer
/// was never admitted, so it has not had a guess at the room code, and the pinned peer the window is for
/// can still come.
///
/// <b>It does re-pair.</b> A device id already pinned to <i>this same</i> certificate is
/// admitted. Pairing the same two certificates again changes nothing that is trusted, and it is
/// how a pairing that completed on one side only gets finished: by simply trying again.
/// </remarks>
internal sealed class PairingTrustWindow
{
    private readonly Lock _gate = new();
    private readonly PeerCertificateStore _pins;
    private readonly string? _expectedDeviceId;
    private readonly TimeSpan _lifetime;
    private readonly PairingAdmission _admission;
    private readonly long _openedAt = Stopwatch.GetTimestamp();
    private readonly TaskCompletionSource<QuicConnection> _claimed =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _consumed;
    private bool _closed;
    private string? _admittedThumbprint;
    private string? _refusalReason;

    /// <param name="pins">The store this window must never contradict.</param>
    /// <param name="expectedDeviceId">
    /// The device the operation is pairing with, when it knows — the master always does, because
    /// it dials a specific agent, and the certificate must then carry that name. Null on the
    /// agent, which learns who the master is from the certificate; there the certificate's own
    /// subject name is the device id.
    /// </param>
    /// <param name="lifetime">How long the window may admit a certificate.</param>
    /// <param name="admission">Whether any certificate may be admitted or only a pinned one.</param>
    public PairingTrustWindow(
        PeerCertificateStore pins,
        string? expectedDeviceId,
        TimeSpan lifetime,
        PairingAdmission admission = PairingAdmission.Anyone)
    {
        ArgumentNullException.ThrowIfNull(pins);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(lifetime, TimeSpan.Zero);

        _pins = pins;
        _expectedDeviceId = expectedDeviceId;
        _lifetime = lifetime;
        _admission = admission;

        // On the master nobody awaits Claimed; a refusal recorded there must not surface later
        // as an unobserved task exception.
        _claimed.Task.ContinueWith(
            static faulted => _ = faulted.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    /// <summary>
    /// Completes with the connection whose certificate this window admitted. Faults with
    /// <see cref="PairingWindowSpentException"/> the moment the window is used up by a
    /// certificate it refuses, so the operation waiting on it finds out at once instead of
    /// waiting for a master that can no longer be let in.
    /// </summary>
    public Task<QuicConnection> Claimed => _claimed.Task;

    /// <summary>Why the one certificate this window was asked about was refused, if it was.</summary>
    public string? RefusalReason
    {
        get
        {
            lock (_gate)
            {
                return _refusalReason;
            }
        }
    }

    /// <summary>True once a certificate has been admitted, whether or not its connection arrived.</summary>
    public bool HasAdmitted
    {
        get
        {
            lock (_gate)
            {
                return _admittedThumbprint is not null;
            }
        }
    }

    /// <summary>
    /// The TLS decision for a certificate presented on a pairing connection. True at most once
    /// in the life of the window.
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

            if (_admission == PairingAdmission.PinnedPeersOnly
                && !_pins.TryGetDeviceIdForThumbprint(PeerCertificateStore.ComputeThumbprint(certificate), out _))
            {
                return false;
            }

            // Consumed by the first certificate it is asked about, admitted or not.
            _consumed = true;

            var refusal = Judge(certificate, out var thumbprint);
            if (refusal is not null)
            {
                _refusalReason = refusal;
                _claimed.TrySetException(new PairingWindowSpentException(refusal));
                return false;
            }

            _admittedThumbprint = thumbprint;
            return true;
        }
    }

    /// <summary>Null to admit; otherwise the reason for refusing, in an operator's words.</summary>
    private string? Judge(X509Certificate certificate, out string thumbprint)
    {
        thumbprint = PeerCertificateStore.ComputeThumbprint(certificate);

        var named = CertificateIdentity.ReadDeviceId(certificate);
        if (named is null)
        {
            return "its certificate names no device";
        }

        if (_expectedDeviceId is not null && !string.Equals(named, _expectedDeviceId, StringComparison.Ordinal))
        {
            return $"its certificate names '{named}', not '{_expectedDeviceId}'";
        }

        if (_pins.TryGetPinnedThumbprint(named, out var pinned))
        {
            // The same certificate again is a re-pair and changes nothing that is trusted.
            return string.Equals(pinned, thumbprint, StringComparison.OrdinalIgnoreCase)
                ? null
                : $"'{named}' is already paired with a different certificate; unpin it before pairing it again";
        }

        if (_pins.TryGetDeviceIdForThumbprint(thumbprint, out var owner))
        {
            return $"its certificate is already paired as '{owner}'";
        }

        return null;
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

/// <summary>A pairing window was used up by a certificate it refused.</summary>
internal sealed class PairingWindowSpentException(string reason) : Exception(reason);

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
    /// a certificate is pinned under, and only after the peer has proved the room code; and
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
