using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Promin.Protocol.Transport;

/// <summary>
/// Who this install is: a device id that is unique to it and never changes, and the certificate
/// whose private key proves it. Both are created on first run and found again ever after.
/// </summary>
/// <remarks>
/// <b>The id.</b> 32 lowercase hex characters from <c>Guid.NewGuid().ToString("N")</c>. It is the
/// certificate's subject common name — pairing refuses a certificate whose name is not the id it
/// dialled — and it is what the other side pins the certificate under, which is why it is unique
/// per install and not a machine name (two machines called "PC-04" must not collide). It is for
/// machines only; a person reads the display name.
///
/// <b>The key never leaves Windows' key store.</b> It is created once as a persisted,
/// non-exportable CNG key (<c>Promin-Device-{id}</c>, in the current user's store, in the
/// Microsoft software provider), so there is exactly one copy of it in the world and no code,
/// this class included, can read it out. The certificate is made around that key and bound to it
/// each time it is loaded. Loading creates nothing: a process that is killed, or never disposes
/// the identity, leaves the key store exactly as it was.
///
/// <b>What is on disk.</b> Two files in the directory: <see cref="IdFileName"/> (the id as text)
/// and <see cref="CertificateFileName"/> (the certificate's public part, DER). That is public
/// data; no private key material is written to any file, ever, so a planted or copied file
/// cannot impersonate this device: an id and certificate are only accepted when a key by that
/// name exists in this user's key store and is the certificate's own key. (That is about secrecy
/// and impersonation, not availability: whoever can write the directory can still make the next
/// start replace the identity, which unpairs the PC, so the directory should be one only the
/// account that owns the identity can write.)
///
/// <b>Anything that does not all agree is replaced.</b> An id with no certificate is useless,
/// and so is a certificate with no id, a certificate that will not load, one whose name is not
/// the id beside it, an id with no key, or a key that is not the certificate's. Each is treated as
/// an empty directory: a new identity (a new id) is created over it, and any key the old identity
/// left in the key store under its name is deleted. The one thing that is not treated that way is
/// a file that is merely unreadable at the moment (locked, access denied): that throws, because
/// replacing a good identity over a transient error would unpair a machine for no reason.
/// </remarks>
public sealed record DeviceIdentity(string DeviceId, X509Certificate2 Certificate) : IDisposable
{
    internal const string IdFileName = "device-id.txt";
    internal const string CertificateFileName = "device-certificate.cer";
    internal const string KeyNamePrefix = "Promin-Device-";

    /// <summary>The length of a device id: <c>Guid.ToString("N")</c>.</summary>
    internal const int DeviceIdLength = 32;

    private const CngKeyOpenOptions UserKey = CngKeyOpenOptions.UserKey;

    // Two callers in one process must not both decide the directory is empty and both create.
    private static readonly Lock Gate = new();

    /// <summary>True for exactly 32 lowercase hex characters, which is all a device id ever is.</summary>
    internal static bool IsValidDeviceId(string? value) =>
        value is { Length: DeviceIdLength } && value.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

    /// <summary>The name of the key store entry that holds the private key of device <paramref name="deviceId"/>.</summary>
    internal static string KeyNameFor(string deviceId) => KeyNamePrefix + deviceId;

    /// <summary>
    /// Returns this install's identity from <paramref name="directory"/>, creating it (and the
    /// directory) on first use. Later calls return the same id and the same certificate, bound to
    /// the same key. The caller owns the result and must dispose it; disposing releases the
    /// certificate's handle on the key and does not delete the key.
    /// </summary>
    /// <remarks>
    /// <b>The identity belongs to the Windows account the app runs under</b>, because that is
    /// whose key store holds the key: use one identity directory per Windows account. Two
    /// accounts sharing one directory is not supported. They would not each get their own
    /// identity; each would find an id and certificate naming a key it does not have, replace
    /// them, and so overwrite the other's files at every start, leaving the other's key in its
    /// key store with nothing naming it. The directory holds only public data.
    /// </remarks>
    public static DeviceIdentity LoadOrCreate(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        var fullDirectory = Path.GetFullPath(directory);

        lock (Gate)
        {
            var staleIds = new HashSet<string>(StringComparer.Ordinal);
            return TryLoad(fullDirectory, staleIds) ?? Create(fullDirectory, staleIds);
        }
    }

    /// <summary>Releases the certificate's handle on the key. The key stays in the key store.</summary>
    public void Dispose() => Certificate.Dispose();

    /// <summary>Deletes the key of device <paramref name="deviceId"/> from the key store, if there is one.</summary>
    internal static void DeleteKey(string deviceId)
    {
        var name = KeyNameFor(deviceId);
        if (CngKey.Exists(name, CngProvider.MicrosoftSoftwareKeyStorageProvider, UserKey))
        {
            using var key = CngKey.Open(name, CngProvider.MicrosoftSoftwareKeyStorageProvider, UserKey);
            key.Delete();
        }
    }

    /// <summary>
    /// Returns the identity the directory holds if it is whole, or null. Whatever ids the
    /// directory names on the way are added to <paramref name="staleIds"/>, so that a replacement
    /// can delete the keys they own.
    /// </summary>
    private static DeviceIdentity? TryLoad(string directory, HashSet<string> staleIds)
    {
        string? idText = null;
        byte[]? certificateBytes = null;
        try
        {
            idText = File.ReadAllText(Path.Combine(directory, IdFileName)).Trim();
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
        }

        try
        {
            certificateBytes = File.ReadAllBytes(Path.Combine(directory, CertificateFileName));
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
        }

        if (IsValidDeviceId(idText))
        {
            staleIds.Add(idText!);
        }

        if (certificateBytes is null)
        {
            return null;
        }

        X509Certificate2 publicCertificate;
        try
        {
            publicCertificate = X509CertificateLoader.LoadCertificate(certificateBytes);
        }
        catch (CryptographicException)
        {
            return null;
        }

        try
        {
            string? certificateId;
            try
            {
                certificateId = CertificateIdentity.ReadDeviceId(publicCertificate);
            }
            catch (CryptographicException)
            {
                // A subject name that cannot be decoded: a damaged certificate like any other.
                return null;
            }

            if (IsValidDeviceId(certificateId))
            {
                staleIds.Add(certificateId!);
            }

            if (!IsValidDeviceId(idText) || !string.Equals(certificateId, idText, StringComparison.Ordinal))
            {
                return null;
            }

            var bound = TryBindToStoredKey(publicCertificate, idText!);
            return bound is null ? null : new DeviceIdentity(idText!, bound);
        }
        finally
        {
            publicCertificate.Dispose();
        }
    }

    /// <summary>
    /// Opens the named key and returns <paramref name="publicCertificate"/> bound to it, or null
    /// if there is no such key or it is not the certificate's key. Creates nothing.
    /// </summary>
    private static X509Certificate2? TryBindToStoredKey(X509Certificate2 publicCertificate, string deviceId)
    {
        var name = KeyNameFor(deviceId);
        if (!CngKey.Exists(name, CngProvider.MicrosoftSoftwareKeyStorageProvider, UserKey))
        {
            return null;
        }

        try
        {
            using var key = CngKey.Open(name, CngProvider.MicrosoftSoftwareKeyStorageProvider, UserKey);
            if (key.Algorithm != CngAlgorithm.ECDsaP256)
            {
                return null;
            }

            using var ecdsa = new ECDsaCng(key);
            return ecdsa.ExportSubjectPublicKeyInfo().AsSpan().SequenceEqual(publicCertificate.PublicKey.ExportSubjectPublicKeyInfo())
                ? publicCertificate.CopyWithPrivateKey(ecdsa)
                : null;
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    private static DeviceIdentity Create(string directory, HashSet<string> staleIds)
    {
        foreach (var staleId in staleIds)
        {
            DeleteKey(staleId);
        }

        var deviceId = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(directory);

        // The order is chosen so that a crash at any point leaves nothing behind that the next
        // start cannot clean up: the id is on disk before the key exists, so a key can never be
        // orphaned without the id that names it; and the certificate, which is what makes the
        // directory an identity, is written last.
        AtomicFile.WriteAllBytes(Path.Combine(directory, IdFileName), System.Text.Encoding.ASCII.GetBytes(deviceId));
        try
        {
            using var key = CngKey.Create(
                CngAlgorithm.ECDsaP256,
                KeyNameFor(deviceId),
                new CngKeyCreationParameters
                {
                    Provider = CngProvider.MicrosoftSoftwareKeyStorageProvider,
                    KeyCreationOptions = CngKeyCreationOptions.None,
                    ExportPolicy = CngExportPolicies.None,
                    KeyUsage = CngKeyUsages.Signing,
                });
            using var ecdsa = new ECDsaCng(key);

            var publicBytes = SelfSignedCertificateFactory.CreatePublicCertificate(deviceId, ecdsa);
            AtomicFile.WriteAllBytes(Path.Combine(directory, CertificateFileName), publicBytes);

            // Bound the same way a later load binds it.
            using var publicCertificate = X509CertificateLoader.LoadCertificate(publicBytes);
            return new DeviceIdentity(deviceId, publicCertificate.CopyWithPrivateKey(ecdsa));
        }
        catch
        {
            DeleteKey(deviceId);
            throw;
        }
    }
}
