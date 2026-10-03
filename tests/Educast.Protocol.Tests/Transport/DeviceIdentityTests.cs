using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;
using Educast.Protocol.Transport;

namespace Educast.Protocol.Tests.Transport;

public sealed partial class DeviceIdentityTests : IDisposable
{
    private readonly List<string> _directories = [];

    [GeneratedRegex("^[0-9a-f]{32}$")]
    private static partial Regex ThirtyTwoLowercaseHex();

    private string NewDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "educast-identity-tests", Guid.NewGuid().ToString("N"));
        _directories.Add(path);
        return path;
    }

    public void Dispose()
    {
        foreach (var directory in _directories)
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void DeviceIdentity_FirstCall_CreatesAnIdOf32LowercaseHex()
    {
        using var identity = DeviceIdentity.LoadOrCreate(NewDirectory());

        Assert.Matches(ThirtyTwoLowercaseHex(), identity.DeviceId);
    }

    [Fact]
    public void DeviceIdentity_FirstCall_UsesTheIdAsTheCertificatesSubjectNameExactly()
    {
        using var identity = DeviceIdentity.LoadOrCreate(NewDirectory());

        // Task 5's pairing refuses a certificate whose common name is not the id it dialled.
        Assert.Equal($"CN={identity.DeviceId}", identity.Certificate.Subject);
        Assert.Equal(identity.DeviceId, identity.Certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false));
        Assert.True(identity.Certificate.HasPrivateKey);
    }

    [Fact]
    public void DeviceIdentity_SecondCall_ReturnsTheSameIdAndThumbprint()
    {
        var directory = NewDirectory();

        using var first = DeviceIdentity.LoadOrCreate(directory);
        using var second = DeviceIdentity.LoadOrCreate(directory);

        Assert.Equal(first.DeviceId, second.DeviceId);
        Assert.Equal(first.Certificate.Thumbprint, second.Certificate.Thumbprint);
        Assert.Equal(
            PeerCertificateStore.ComputeThumbprint(first.Certificate),
            PeerCertificateStore.ComputeThumbprint(second.Certificate));
    }

    /// <summary>
    /// The reloaded certificate has to carry the same private key, not just the same public
    /// part: a certificate that lost its key on the way back from disk would pin and dial fine
    /// and then fail every handshake.
    /// </summary>
    [Fact]
    public void DeviceIdentity_SecondCall_ReturnsACertificateWhoseKeyStillSignsForTheSamePublicKey()
    {
        var directory = NewDirectory();
        using var first = DeviceIdentity.LoadOrCreate(directory);
        using var second = DeviceIdentity.LoadOrCreate(directory);

        Assert.True(second.Certificate.HasPrivateKey);

        byte[] message = [9, 8, 7];
        using var reloadedKey = second.Certificate.GetECDsaPrivateKey();
        Assert.NotNull(reloadedKey);
        var signature = reloadedKey.SignData(message, HashAlgorithmName.SHA256);

        // Verified with the public key of the certificate from the FIRST call.
        using var originalPublicKey = first.Certificate.GetECDsaPublicKey();
        Assert.True(originalPublicKey!.VerifyData(message, signature, HashAlgorithmName.SHA256));
    }

    /// <summary>
    /// SChannel is the arbiter of whether a stored-and-reloaded certificate is usable as a TLS
    /// credential (it is picky about key containers), so prove it with a real handshake: the
    /// listener's start-up check is a TLS handshake against itself over loopback.
    /// </summary>
    [Fact]
    public async Task DeviceIdentity_ReloadedCertificate_CompletesATlsHandshake()
    {
        var directory = NewDirectory();
        using (DeviceIdentity.LoadOrCreate(directory))
        {
        }

        using var reloaded = DeviceIdentity.LoadOrCreate(directory);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        // Port 0 on loopback: no fixed port and no wildcard bind, so no firewall prompt.
        await using var listener = new QuicAgentListener(
            reloaded.Certificate, new PeerCertificateStore(), port: 0, IPAddress.Loopback);
        await listener.StartAsync(deadline.Token);

        Assert.NotEqual(0, listener.LocalEndPoint.Port);
    }

    [Fact]
    public void DeviceIdentity_ReloadedCertificate_KeepsItsKeyNonExportable()
    {
        var directory = NewDirectory();
        using (DeviceIdentity.LoadOrCreate(directory))
        {
        }

        using var reloaded = DeviceIdentity.LoadOrCreate(directory);
        using var key = reloaded.Certificate.GetECDsaPrivateKey();

        Assert.NotNull(key);
        Assert.ThrowsAny<CryptographicException>(() => key.ExportPkcs8PrivateKey());
    }

    [Fact]
    public void DeviceIdentity_TwoDirectories_GetDifferentIds()
    {
        using var one = DeviceIdentity.LoadOrCreate(NewDirectory());
        using var two = DeviceIdentity.LoadOrCreate(NewDirectory());

        Assert.NotEqual(one.DeviceId, two.DeviceId);
        Assert.NotEqual(one.Certificate.Thumbprint, two.Certificate.Thumbprint);
    }

    [Fact]
    public void DeviceIdentity_IdWithoutCertificate_IsRecreated()
    {
        var directory = NewDirectory();
        string firstId;
        using (var first = DeviceIdentity.LoadOrCreate(directory))
        {
            firstId = first.DeviceId;
        }

        File.Delete(Path.Combine(directory, DeviceIdentity.CertificateFileName));

        using var second = DeviceIdentity.LoadOrCreate(directory);

        Assert.NotEqual(firstId, second.DeviceId);
        Assert.Matches(ThirtyTwoLowercaseHex(), second.DeviceId);
        Assert.Equal($"CN={second.DeviceId}", second.Certificate.Subject);

        // And it is a stable identity from here on.
        using var third = DeviceIdentity.LoadOrCreate(directory);
        Assert.Equal(second.DeviceId, third.DeviceId);
    }

    [Fact]
    public void DeviceIdentity_CertificateWithoutId_IsRecreated()
    {
        var directory = NewDirectory();
        string firstId;
        using (var first = DeviceIdentity.LoadOrCreate(directory))
        {
            firstId = first.DeviceId;
        }

        File.Delete(Path.Combine(directory, DeviceIdentity.IdFileName));

        using var second = DeviceIdentity.LoadOrCreate(directory);

        Assert.NotEqual(firstId, second.DeviceId);
        Assert.Equal($"CN={second.DeviceId}", second.Certificate.Subject);
    }

    [Fact]
    public void DeviceIdentity_CorruptCertificateFile_IsRecreated()
    {
        var directory = NewDirectory();
        string firstId;
        using (var first = DeviceIdentity.LoadOrCreate(directory))
        {
            firstId = first.DeviceId;
        }

        File.WriteAllBytes(Path.Combine(directory, DeviceIdentity.CertificateFileName), [1, 2, 3, 4]);

        using var second = DeviceIdentity.LoadOrCreate(directory);

        Assert.NotEqual(firstId, second.DeviceId);
        Assert.True(second.Certificate.HasPrivateKey);
    }

    [Theory]
    [InlineData("")]
    [InlineData("agent-1")]
    [InlineData("0123456789ABCDEF0123456789ABCDEF")]
    public void DeviceIdentity_IdFileThatIsNotAnId_IsRecreated(string content)
    {
        var directory = NewDirectory();
        string firstId;
        using (var first = DeviceIdentity.LoadOrCreate(directory))
        {
            firstId = first.DeviceId;
        }

        File.WriteAllText(Path.Combine(directory, DeviceIdentity.IdFileName), content);

        using var second = DeviceIdentity.LoadOrCreate(directory);

        Assert.NotEqual(firstId, second.DeviceId);
        Assert.Matches(ThirtyTwoLowercaseHex(), second.DeviceId);
    }

    /// <summary>
    /// An id that does not match the certificate beside it (a half-finished replace, or two
    /// files copied from different machines) would fail every pairing, so the pair is not kept.
    /// </summary>
    [Fact]
    public void DeviceIdentity_CertificateForADifferentId_IsRecreated()
    {
        var directoryOne = NewDirectory();
        var directoryTwo = NewDirectory();
        string idOne;
        using (var one = DeviceIdentity.LoadOrCreate(directoryOne))
        using (var two = DeviceIdentity.LoadOrCreate(directoryTwo))
        {
            idOne = one.DeviceId;
        }

        // Directory two now holds directory one's id beside directory two's certificate.
        File.Copy(
            Path.Combine(directoryOne, DeviceIdentity.IdFileName),
            Path.Combine(directoryTwo, DeviceIdentity.IdFileName),
            overwrite: true);

        using var recreated = DeviceIdentity.LoadOrCreate(directoryTwo);

        Assert.NotEqual(idOne, recreated.DeviceId);
        Assert.Equal($"CN={recreated.DeviceId}", recreated.Certificate.Subject);
    }

    [Fact]
    public void DeviceIdentity_CreatesTheDirectoryAndLeavesOnlyTheTwoFilesInIt()
    {
        var directory = Path.Combine(NewDirectory(), "nested", "identity");

        using var identity = DeviceIdentity.LoadOrCreate(directory);
        using var again = DeviceIdentity.LoadOrCreate(directory);

        var names = Directory.GetFileSystemEntries(directory).Select(Path.GetFileName).Order().ToArray();
        Assert.Equal(
            new[] { DeviceIdentity.CertificateFileName, DeviceIdentity.IdFileName }.Order().ToArray(),
            names);
    }

    [Fact]
    public void DeviceIdentity_StoresTheIdAsPlainText_AndTheCertificateAsPkcs12WithItsKey()
    {
        var directory = NewDirectory();
        using var identity = DeviceIdentity.LoadOrCreate(directory);

        Assert.Equal(identity.DeviceId, File.ReadAllText(Path.Combine(directory, DeviceIdentity.IdFileName)).Trim());

        // Loaded back by hand, the file yields a certificate with a usable private key: that is
        // what makes it the only copy of the key that survives a restart.
        using var fromFile = X509CertificateLoader.LoadPkcs12FromFile(
            Path.Combine(directory, DeviceIdentity.CertificateFileName),
            password: null,
            keyStorageFlags: X509KeyStorageFlags.EphemeralKeySet);
        Assert.True(fromFile.HasPrivateKey);
        Assert.Equal(identity.Certificate.Thumbprint, fromFile.Thumbprint);
    }

    [Fact]
    public void DeviceIdentity_Dispose_ReleasesTheCertificate()
    {
        var identity = DeviceIdentity.LoadOrCreate(NewDirectory());
        var certificate = identity.Certificate;

        identity.Dispose();

        Assert.Throws<CryptographicException>(() => certificate.GetECDsaPrivateKey());
    }

    [Fact]
    public void DeviceIdentity_RejectsAnEmptyDirectoryName()
    {
        Assert.Throws<ArgumentException>(() => DeviceIdentity.LoadOrCreate(""));
        Assert.Throws<ArgumentNullException>(() => DeviceIdentity.LoadOrCreate(null!));
    }
}
