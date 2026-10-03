using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Educast.Protocol.Transport;

/// <summary>
/// Creates the self-signed certificate a device presents on every QUIC handshake.
/// Trust never comes from this certificate's issuer — it comes from PSK pairing plus
/// trust-on-first-use pinning of the certificate's thumbprint (see
/// <see cref="PeerCertificateStore"/>). The certificate exists only to give TLS 1.3 a
/// key to authenticate the channel with, and to give pinning something stable to pin.
/// </summary>
/// <remarks>
/// ECDSA P-256 rather than RSA-2048: equivalent security at a fraction of the handshake
/// cost, which matters on the dual-core classroom hardware this ships to. The certificate
/// carries a Subject Alternative Name and the serverAuth/clientAuth EKUs because a TLS
/// stack is entitled to reject a certificate that has neither, and the same certificate is
/// used in both roles (the agent is the TLS server, the master is the TLS client).
///
/// The returned certificate owns a private key that is <b>not</b> persisted to disk:
/// the PKCS#12 round-trip below deliberately omits <see cref="X509KeyStorageFlags.PersistKeySet"/>,
/// so the key container is torn down when the certificate is disposed. Callers must dispose it.
/// The key is not exportable either.
/// </remarks>
public static class SelfSignedCertificateFactory
{
    private const string ServerAuthenticationOid = "1.3.6.1.5.5.7.3.1";
    private const string ClientAuthenticationOid = "1.3.6.1.5.5.7.3.2";

    /// <summary>How long a freshly created device certificate stays valid.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(365 * 10);

    public static X509Certificate2 Create(string subjectName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subjectName);

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var certificate = BuildSelfSigned(subjectName, key);

        // SChannel (which msquic uses on Windows) will only accept a TLS credential whose
        // private key lives in a key container, so the in-memory key from CreateSelfSigned
        // has to be round-tripped through PKCS#12. PersistKeySet is deliberately absent:
        // the container is temporary and goes away with the certificate handle. Exportable is
        // deliberately absent too: the key never needs to leave the container it lands in.
        // (EphemeralKeySet is not an option: SChannel refuses such a credential, 0x8009030D.)
        return X509CertificateLoader.LoadPkcs12(
            certificate.Export(X509ContentType.Pfx),
            password: null,
            keyStorageFlags: X509KeyStorageFlags.DefaultKeySet);
    }

    /// <summary>
    /// The public part (DER, no private key) of a certificate with exactly the shape
    /// <see cref="Create"/> gives, signed by <paramref name="key"/>. For a key that already lives
    /// in a key store and never leaves it (see <see cref="DeviceIdentity"/>): the caller keeps the
    /// bytes and binds the certificate to the key with
    /// <see cref="X509Certificate2.CopyWithPrivateKey(ECDsa)"/>. The key must be on the NIST
    /// P-256 curve.
    /// </summary>
    internal static byte[] CreatePublicCertificate(string subjectName, ECDsa key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subjectName);
        ArgumentNullException.ThrowIfNull(key);

        using var certificate = BuildSelfSigned(subjectName, key);
        return certificate.Export(X509ContentType.Cert);
    }

    /// <summary>The one definition of what a device certificate looks like.</summary>
    private static X509Certificate2 BuildSelfSigned(string subjectName, ECDsa key)
    {
        var request = new CertificateRequest($"CN={subjectName}", key, HashAlgorithmName.SHA256);

        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(
            certificateAuthority: false, hasPathLengthConstraint: false, pathLengthConstraint: 0, critical: true));

        // DigitalSignature only. An ECDSA key signs; it cannot encipher a key, so asserting
        // KeyEncipherment would be meaningless — and this extension is critical.
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature, critical: true));

        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            [new Oid(ServerAuthenticationOid), new Oid(ClientAuthenticationOid)], critical: false));

        var subjectAlternativeNames = new SubjectAlternativeNameBuilder();
        subjectAlternativeNames.AddDnsName(subjectName);
        request.CertificateExtensions.Add(subjectAlternativeNames.Build());

        var now = DateTimeOffset.UtcNow;
        return request.CreateSelfSigned(now.AddMinutes(-5), now.Add(Lifetime));
    }
}
