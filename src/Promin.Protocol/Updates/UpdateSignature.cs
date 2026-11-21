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
    // TODO(#37): the owner's key goes here; until then nothing verifies.
    public static ReadOnlyMemory<byte> ProjectPublicKey { get; } = Array.Empty<byte>();

    /// <summary>What is signed: UTF-8 <c>promin-setup\n{version}\n{sha256 as lowercase hex}</c>.</summary>
    public static byte[] SignedBytes(ReleaseVersion version, ReadOnlySpan<byte> sha256)
    {
        ArgumentNullException.ThrowIfNull(version);
        return Encoding.UTF8.GetBytes($"promin-setup\n{version}\n{Convert.ToHexStringLower(sha256)}");
    }

    /// <summary>
    /// True when <paramref name="signature"/> is <paramref name="publicKey"/>'s signature on the file at
    /// <paramref name="setupPath"/> as <paramref name="version"/>. False for a missing or unreadable file, and for a
    /// malformed key or signature.
    /// </summary>
    public static bool HoldsFor(string setupPath, ReleaseVersion version, ReadOnlySpan<byte> signature, ReadOnlySpan<byte> publicKey)
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
            return key.VerifyData(SignedBytes(version, sha256), signature, HashAlgorithmName.SHA256);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }
}
