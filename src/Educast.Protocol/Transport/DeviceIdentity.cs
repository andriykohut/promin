using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Educast.Protocol.Transport;

/// <summary>
/// Who this install is: a device id that is unique to it and never changes, and the certificate
/// (with its private key) that proves it. Both are created on first run and reloaded ever after.
/// </summary>
/// <remarks>
/// <b>The id.</b> 32 lowercase hex characters from <c>Guid.NewGuid().ToString("N")</c>. It is the
/// certificate's subject common name — pairing refuses a certificate whose name is not the id it
/// dialled — and it is what the other side pins the certificate under, which is why it is unique
/// per install and not a machine name (two machines called "PC-04" must not collide). It is for
/// machines only; a person reads the display name.
///
/// <b>What is on disk.</b> Two files in <c>directory</c>: <see cref="IdFileName"/> (the id as
/// text) and <see cref="CertificateFileName"/> (the certificate and its key as an unencrypted
/// PKCS#12 image). The certificate file <b>is private key material</b>: the directory must be
/// one only this user can read (for the agent, a per-user or per-service data directory, never a
/// shared one). The key is written nowhere else, is never logged and is never put in an
/// exception message.
///
/// <b>Why the key can be saved at all.</b> The certificate the factory returns has a deliberately
/// non-exportable key, so this class cannot export it afterwards. It takes the PKCS#12 image from
/// the factory at the one moment the key is still exportable, when the certificate is made. Loading it
/// back uses the same flags as the factory (<see cref="X509KeyStorageFlags.DefaultKeySet"/>, the
/// only mode SChannel accepts), so the reloaded certificate behaves the same as a fresh one: a
/// key container that exists only while the certificate is alive and is removed when it is
/// disposed. Nothing is added to the machine key store permanently, and the PKCS#12 file is the
/// single copy of the key that outlives the process.
///
/// <b>An id with no certificate is useless</b>, and so is a certificate with no id, a certificate
/// that will not load, and a certificate whose name is not the id beside it. Any of those is
/// treated as an empty directory and a new identity (a new id) is created over it. The one
/// thing that is not treated that way is a file that is merely unreadable at the moment (locked,
/// access denied): that throws, because replacing a good identity over a transient error would
/// unpair a machine for no reason.
/// </remarks>
public sealed record DeviceIdentity(string DeviceId, X509Certificate2 Certificate) : IDisposable
{
    internal const string IdFileName = "device-id.txt";
    internal const string CertificateFileName = "device-certificate.pfx";

    // Two callers in one process must not both decide the directory is empty and both create.
    private static readonly Lock Gate = new();

    /// <summary>
    /// Returns this install's identity from <paramref name="directory"/>, creating it (and the
    /// directory) on first use. Later calls return the same id and the same certificate. The
    /// caller owns the result and must dispose it.
    /// </summary>
    public static DeviceIdentity LoadOrCreate(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        var fullDirectory = Path.GetFullPath(directory);

        lock (Gate)
        {
            return TryLoad(fullDirectory) ?? Create(fullDirectory);
        }
    }

    /// <summary>The length of a device id: <c>Guid.ToString("N")</c>.</summary>
    internal const int DeviceIdLength = 32;

    /// <summary>True for exactly 32 lowercase hex characters, which is all a device id ever is.</summary>
    internal static bool IsValidDeviceId(string? value) =>
        value is { Length: DeviceIdLength } && value.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

    /// <summary>Releases the certificate and with it the key container behind it.</summary>
    public void Dispose() => Certificate.Dispose();

    private static DeviceIdentity? TryLoad(string directory)
    {
        string deviceId;
        byte[] pkcs12;
        try
        {
            deviceId = File.ReadAllText(Path.Combine(directory, IdFileName)).Trim();
            pkcs12 = File.ReadAllBytes(Path.Combine(directory, CertificateFileName));
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }

        try
        {
            if (!IsValidDeviceId(deviceId))
            {
                return null;
            }

            X509Certificate2 certificate;
            try
            {
                certificate = X509CertificateLoader.LoadPkcs12(
                    pkcs12, password: null, keyStorageFlags: X509KeyStorageFlags.DefaultKeySet);
            }
            catch (CryptographicException)
            {
                return null;
            }

            if (certificate.HasPrivateKey
                && string.Equals(
                    certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false),
                    deviceId,
                    StringComparison.Ordinal))
            {
                return new DeviceIdentity(deviceId, certificate);
            }

            certificate.Dispose();
            return null;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pkcs12);
        }
    }

    private static DeviceIdentity Create(string directory)
    {
        var deviceId = Guid.NewGuid().ToString("N");
        var certificate = SelfSignedCertificateFactory.Create(deviceId, out var pkcs12);
        try
        {
            Directory.CreateDirectory(directory);

            // The certificate first, the id last: the id file is what says "this directory holds
            // an identity", and a load also checks that the two agree, so a crash between the
            // two writes is recovered from by making a new identity.
            WriteAtomically(Path.Combine(directory, CertificateFileName), pkcs12);
            WriteAtomically(Path.Combine(directory, IdFileName), System.Text.Encoding.ASCII.GetBytes(deviceId));
            return new DeviceIdentity(deviceId, certificate);
        }
        catch
        {
            certificate.Dispose();
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pkcs12);
        }
    }

    /// <summary>
    /// The same atomic write <see cref="PeerCertificateStore.Save"/> uses: a complete temporary
    /// file beside the destination, then swapped in, so a crash or a full disk leaves the
    /// previous file intact rather than a truncated one. The temporary file is removed whatever
    /// happens, since for the certificate it holds a private key.
    /// </summary>
    private static void WriteAtomically(string path, byte[] content)
    {
        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllBytes(temporaryPath, content);

            if (File.Exists(path))
            {
                File.Replace(temporaryPath, path, destinationBackupFileName: null);
            }
            else
            {
                File.Move(temporaryPath, path);
            }
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}
