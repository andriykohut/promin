using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;
using Educast.Protocol.Transport;

namespace Educast.Protocol.Tests.Transport;

/// <summary>
/// Every test in this class creates real, persisted keys in the current Windows user's key
/// store, so the class runs alone (nothing else in the run is creating keys while a test counts
/// them) and every key a test makes is deleted in <see cref="Dispose"/>, which xunit runs
/// whether the test passed or failed.
/// </summary>
[Collection(KeyStoreCollection.Name)]
public sealed partial class DeviceIdentityTests : IDisposable
{
    private readonly List<string> _directories = [];
    private readonly List<string> _keyNamesToDelete = [];

    [GeneratedRegex("^[0-9a-f]{32}$")]
    private static partial Regex ThirtyTwoLowercaseHex();

    /// <summary>The key name the identity design promises: stable, derived from the id.</summary>
    internal static string KeyNameFor(string deviceId) => $"Educast-Device-{deviceId}";

    private string NewDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "educast-identity-tests", Guid.NewGuid().ToString("N"));
        _directories.Add(path);
        return path;
    }

    public void Dispose()
    {
        // Whatever ids the directories name now, whether or not the test is still holding the
        // identity, is a key this test may have created.
        foreach (var directory in _directories)
        {
            if (!Directory.Exists(directory))
            {
                continue;
            }

            foreach (var file in Directory.GetFiles(directory, "*", SearchOption.AllDirectories))
            {
                try
                {
                    var text = File.ReadAllText(file).Trim();
                    if (ThirtyTwoLowercaseHex().IsMatch(text))
                    {
                        _keyNamesToDelete.Add(KeyNameFor(text));
                    }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                }

                try
                {
                    using var certificate = X509CertificateLoader.LoadCertificateFromFile(file);
                    _keyNamesToDelete.Add(KeyNameFor(certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false)));
                }
                catch (Exception e) when (e is CryptographicException or IOException)
                {
                }
            }
        }

        var failures = new List<Exception>();
        foreach (var name in _keyNamesToDelete.Distinct())
        {
            try
            {
                DeleteKeyNamed(name);
            }
            catch (Exception e)
            {
                failures.Add(e);
            }
        }

        foreach (var directory in _directories)
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }

        var parent = Path.Combine(Path.GetTempPath(), "educast-identity-tests");
        if (Directory.Exists(parent) && !Directory.EnumerateFileSystemEntries(parent).Any())
        {
            try
            {
                Directory.Delete(parent);
            }
            catch (IOException)
            {
                // Another test is using it at this moment.
            }
        }

        if (failures.Count > 0)
        {
            throw new AggregateException("A test key could not be deleted from the key store.", failures);
        }
    }

    private static void DeleteKeyNamed(string name)
    {
        if (CngKey.Exists(name))
        {
            using var key = CngKey.Open(name);
            key.Delete();
        }
    }

    private static bool KeyExists(string deviceId) => CngKey.Exists(KeyNameFor(deviceId));

    private static string CngKeyDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Microsoft", "Crypto", "Keys");

    private static int KeyFileCount() => Directory.GetFiles(CngKeyDirectory).Length;

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

        // Task 5's pairing refuses a certificate whose common name is not the id it dialled,
        // and reads that name the way CertificateIdentity does.
        Assert.Equal($"CN={identity.DeviceId}", identity.Certificate.Subject);
        Assert.Equal(identity.DeviceId, CertificateIdentity.ReadDeviceId(identity.Certificate));
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
    /// part: a certificate that lost its key on the way back would pin and dial fine and then
    /// fail every handshake.
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

        using var originalPublicKey = first.Certificate.GetECDsaPublicKey();
        Assert.True(originalPublicKey!.VerifyData(message, signature, HashAlgorithmName.SHA256));
    }

    /// <summary>
    /// SChannel is the arbiter of whether a certificate bound to a stored key is usable as a TLS
    /// credential, so prove it with a real handshake: the listener's start-up check is a TLS
    /// handshake against itself over loopback.
    /// </summary>
    [Fact]
    public async Task ReloadedIdentity_CompletesARealTlsHandshake()
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
    public void TheKey_CannotBeExported()
    {
        var directory = NewDirectory();
        using var fresh = DeviceIdentity.LoadOrCreate(directory);
        using var reloaded = DeviceIdentity.LoadOrCreate(directory);

        foreach (var certificate in new[] { fresh.Certificate, reloaded.Certificate })
        {
            using var key = certificate.GetECDsaPrivateKey();
            Assert.NotNull(key);
            Assert.ThrowsAny<CryptographicException>(() => key.ExportPkcs8PrivateKey());
            Assert.ThrowsAny<CryptographicException>(() => key.ExportParameters(includePrivateParameters: true));
        }

        // And the key store itself says so: a persisted, current-user key whose export policy
        // is none, in the Microsoft software provider.
        using var stored = CngKey.Open(KeyNameFor(fresh.DeviceId));
        Assert.Equal(CngExportPolicies.None, stored.ExportPolicy);
        Assert.False(stored.IsMachineKey);
        Assert.Equal(CngProvider.MicrosoftSoftwareKeyStorageProvider, stored.Provider);
        Assert.Equal(CngAlgorithm.ECDsaP256, stored.Algorithm);
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
        Assert.False(KeyExists(firstId), "the replaced identity's key must not be left in the key store");

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
        Assert.False(KeyExists(firstId), "the replaced identity's key must not be left in the key store");
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
        Assert.False(KeyExists(firstId), "the replaced identity's key must not be left in the key store");
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
    public void AnIdentityWhoseKeyIsMissing_IsRecreated()
    {
        var directory = NewDirectory();
        string firstId;
        using (var first = DeviceIdentity.LoadOrCreate(directory))
        {
            firstId = first.DeviceId;
        }

        DeleteKeyNamed(KeyNameFor(firstId));

        using var second = DeviceIdentity.LoadOrCreate(directory);

        Assert.NotEqual(firstId, second.DeviceId);
        Assert.True(second.Certificate.HasPrivateKey);
        Assert.True(KeyExists(second.DeviceId));
    }

    /// <summary>
    /// A key that is present under the right name but is not the certificate's key (the
    /// certificate's public key must equal the key's) is not this identity's key.
    /// </summary>
    [Fact]
    public void AKeyThatIsNotTheCertificatesKey_IsReplacedAlongWithTheIdentity()
    {
        var directory = NewDirectory();
        string firstId;
        using (var first = DeviceIdentity.LoadOrCreate(directory))
        {
            firstId = first.DeviceId;
        }

        var name = KeyNameFor(firstId);
        DeleteKeyNamed(name);
        using (CngKey.Create(CngAlgorithm.ECDsaP256, name, new CngKeyCreationParameters
        {
            ExportPolicy = CngExportPolicies.None,
            KeyUsage = CngKeyUsages.Signing,
            Provider = CngProvider.MicrosoftSoftwareKeyStorageProvider,
        }))
        {
        }

        using var second = DeviceIdentity.LoadOrCreate(directory);

        Assert.NotEqual(firstId, second.DeviceId);
        Assert.False(KeyExists(firstId), "the mismatched key must be deleted with the identity it belonged to");
    }

    /// <summary>
    /// Someone who can write the identity directory plants an id and a certificate they hold
    /// the key to. Nothing in the key store is named for that id, so the pair is not this
    /// device's identity and is not accepted.
    /// </summary>
    [Fact]
    public void APlantedIdentity_IsNotAccepted()
    {
        var directory = NewDirectory();
        Directory.CreateDirectory(directory);
        var plantedId = Guid.NewGuid().ToString("N");
        using (var planted = SelfSignedCertificateFactory.Create(plantedId))
        {
            File.WriteAllBytes(
                Path.Combine(directory, DeviceIdentity.CertificateFileName), planted.Export(X509ContentType.Cert));
        }

        File.WriteAllText(Path.Combine(directory, DeviceIdentity.IdFileName), plantedId);

        using var loaded = DeviceIdentity.LoadOrCreate(directory);

        Assert.NotEqual(plantedId, loaded.DeviceId);
        Assert.Equal(loaded.DeviceId, File.ReadAllText(Path.Combine(directory, DeviceIdentity.IdFileName)).Trim());
    }

    [Fact]
    public void TheIdentityDirectory_HoldsNoPrivateKey()
    {
        var directory = NewDirectory();
        using (DeviceIdentity.LoadOrCreate(directory))
        {
        }

        using (DeviceIdentity.LoadOrCreate(directory))
        {
        }

        var files = Directory.GetFiles(directory);
        Assert.NotEmpty(files);
        foreach (var file in files)
        {
            // No file loads as a certificate that has a private key...
            try
            {
                using var asPfx = X509CertificateLoader.LoadPkcs12FromFile(
                    file, password: null, keyStorageFlags: X509KeyStorageFlags.EphemeralKeySet);
                Assert.False(asPfx.HasPrivateKey, $"{Path.GetFileName(file)} loads as a certificate with a private key");
            }
            catch (CryptographicException)
            {
            }

            try
            {
                using var asCertificate = X509CertificateLoader.LoadCertificateFromFile(file);
                Assert.False(asCertificate.HasPrivateKey);
            }
            catch (CryptographicException)
            {
            }

            // ...and none carries a PKCS#8 / PEM private key either.
            var text = System.Text.Encoding.Latin1.GetString(File.ReadAllBytes(file));
            Assert.DoesNotContain("PRIVATE KEY", text);
        }

        // A P-256 certificate is a few hundred bytes and an id is 32: public data only.
        Assert.True(
            files.Sum(f => new FileInfo(f).Length) < 2048,
            "the identity directory is bigger than a public certificate and an id can be");
    }

    /// <summary>
    /// The point of the design. Reloading must create nothing in the key store: twenty loads
    /// that never dispose leave exactly the one key the identity was created with. (A process
    /// that is killed never disposes anything; this is what that looks like twenty times.)
    /// </summary>
    [Fact]
    public void LoadingManyTimes_WithoutDisposing_LeavesExactlyOneKey()
    {
        var directory = NewDirectory();
        var baseline = KeyFileCount();

        DeviceIdentity created = DeviceIdentity.LoadOrCreate(directory);
        var afterCreation = KeyFileCount();

        var held = new List<DeviceIdentity> { created };
        for (var i = 0; i < 20; i++)
        {
            held.Add(DeviceIdentity.LoadOrCreate(directory));    // deliberately never disposed
        }

        var afterLoads = KeyFileCount();
        Assert.Equal(1, afterCreation - baseline);
        Assert.Equal(afterCreation, afterLoads);
        Assert.All(held, identity => Assert.Equal(created.DeviceId, identity.DeviceId));

        // Letting go of them, by finalizer or by Dispose, does not remove the identity's key.
        foreach (var identity in held)
        {
            identity.Dispose();
        }

        held.Clear();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        Assert.Equal(afterCreation, KeyFileCount());
        Assert.True(KeyExists(created.DeviceId));
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
    public void DeviceIdentity_StoresTheIdAsPlainText_AndTheCertificateAsItsPublicPartOnly()
    {
        var directory = NewDirectory();
        using var identity = DeviceIdentity.LoadOrCreate(directory);

        Assert.Equal(identity.DeviceId, File.ReadAllText(Path.Combine(directory, DeviceIdentity.IdFileName)).Trim());

        using var fromFile = X509CertificateLoader.LoadCertificateFromFile(
            Path.Combine(directory, DeviceIdentity.CertificateFileName));
        Assert.False(fromFile.HasPrivateKey);
        Assert.Equal(identity.Certificate.Thumbprint, fromFile.Thumbprint);
    }

    [Fact]
    public void DeviceIdentity_Dispose_ReleasesTheCertificate_ButNotTheKey()
    {
        var identity = DeviceIdentity.LoadOrCreate(NewDirectory());
        var certificate = identity.Certificate;
        var id = identity.DeviceId;

        identity.Dispose();

        Assert.Throws<CryptographicException>(() => certificate.GetECDsaPrivateKey());
        Assert.True(KeyExists(id));
    }

    [Fact]
    public void DeviceIdentity_RejectsAnEmptyDirectoryName()
    {
        Assert.Throws<ArgumentException>(() => DeviceIdentity.LoadOrCreate(""));
        Assert.Throws<ArgumentNullException>(() => DeviceIdentity.LoadOrCreate(null!));
    }
}

/// <summary>
/// The tests that count the key store's files, and every test that creates keys in it, run one
/// at a time and after everything that runs in parallel has finished.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class KeyStoreCollection
{
    public const string Name = "Windows key store";
}
