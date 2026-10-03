using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Educast.Protocol.Transport;

/// <summary>
/// The bytes that tie a pairing handshake to the two certificates at the ends of the TLS
/// connection it ran over:
/// <code>
/// binding = SHA256(masterCertDER) || SHA256(agentCertDER)      // 64 bytes, fixed
/// </code>
/// </summary>
/// <remarks>
/// Each side builds this from <b>its own</b> certificate for its own half and <b>the certificate
/// it actually received over this TLS connection</b> for the peer's half — never from a value
/// the peer sent in a message. When the two ends are talking to each other directly, they build
/// the same 64 bytes and the handshake proofs verify. When something sits between them with a
/// certificate of its own, each end has received the middle party's certificate in place of the
/// other's, the two bindings differ, and neither proof verifies: the middle party cannot make
/// itself the certificate that gets pinned, however faithfully it forwards the messages.
/// </remarks>
internal static class CertificateBinding
{
    /// <summary>Two SHA-256 digests.</summary>
    public const int Length = 2 * SHA256.HashSizeInBytes;

    public static byte[] Compute(X509Certificate masterCertificate, X509Certificate agentCertificate)
    {
        ArgumentNullException.ThrowIfNull(masterCertificate);
        ArgumentNullException.ThrowIfNull(agentCertificate);

        var binding = new byte[Length];
        SHA256.HashData(masterCertificate.GetRawCertData(), binding.AsSpan(0, SHA256.HashSizeInBytes));
        SHA256.HashData(agentCertificate.GetRawCertData(), binding.AsSpan(SHA256.HashSizeInBytes));
        return binding;
    }
}
