using System.Security.Cryptography;
using System.Text;

namespace Promin.Protocol.Updates;

/// <summary>
/// The project's signature on a release's setup, which the agent and the console check before running one. It is an
/// ECDSA P-256 signature (SHA-256, IEEE P1363 format) over <see cref="SignedBytes"/>, made by
/// <c>installer/sign-setup.ps1</c> in the release workflow with the key held in the repository's secrets.
/// </summary>
public static class UpdateSignature
{
    /// <summary>The project's public key (SubjectPublicKeyInfo, DER), as <c>installer/new-signing-key.ps1</c> prints it.</summary>
    // installer/sign-setup.ps1 reads this literal and refuses to sign with any other key.
    public static ReadOnlyMemory<byte> ProjectPublicKey { get; } =
        Convert.FromBase64String("MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEznBc57Uopapy3FXBzp1OARWd2FQjyXdT9chvqk+p4TiDs709F5a5uq5o2N8DaRuDudWOphsEmsBrVzsS7BHuNg==");

    /// <summary>
    /// What is signed: UTF-8 <c>promin-setup\n{setup name}\n{version}\n{sha256 as lowercase hex}</c>, the setup
    /// name being the release asset's file name, so a signature on one setup does not hold for the other.
    /// </summary>
    public static byte[] SignedBytes(string setupName, ReleaseVersion version, ReadOnlySpan<byte> sha256)
    {
        ArgumentNullException.ThrowIfNull(setupName);
        ArgumentNullException.ThrowIfNull(version);
        return Encoding.UTF8.GetBytes($"promin-setup\n{setupName}\n{version}\n{Convert.ToHexStringLower(sha256)}");
    }

    /// <summary>
    /// True when <paramref name="signature"/> is <paramref name="publicKey"/>'s signature on the file at
    /// <paramref name="setupPath"/> as the setup named <paramref name="setupName"/> at <paramref name="version"/>. False
    /// for a missing or unreadable file, and for a malformed key or signature.
    /// </summary>
    public static bool HoldsFor(
        string setupPath, string setupName, ReleaseVersion version, ReadOnlySpan<byte> signature, ReadOnlySpan<byte> publicKey)
    {
        ArgumentNullException.ThrowIfNull(setupPath);
        ArgumentNullException.ThrowIfNull(version);

        byte[] sha256;
        try
        {
            using var file = File.OpenRead(setupPath);
            sha256 = SHA256.HashData(file);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(publicKey, out _);
            return key.VerifyData(SignedBytes(setupName, version, sha256), signature, HashAlgorithmName.SHA256);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }
}
