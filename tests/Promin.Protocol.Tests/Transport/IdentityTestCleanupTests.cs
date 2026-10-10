using System.Security.Cryptography;
using Promin.Protocol.Transport;

namespace Promin.Protocol.Tests.Transport;

/// <summary>
/// The clean-up the identity tests rely on is itself tested, because a clean-up that deletes too
/// little leaves clutter in a person's key store and one that deletes too much is worse. These
/// tests create real keys, so they run in the key-store collection and delete what they make.
/// </summary>
[Collection(KeyStoreCollection.Name)]
public sealed class IdentityTestCleanupTests : IDisposable
{
    private readonly string _root = Path.Combine(
        IdentityTestCleanup.TempRoot("promin-identity-cleanup-tests"), Guid.NewGuid().ToString("N"));
    private readonly List<string> _ids = [];
    private readonly List<string> _oddKeyNames = [];

    public void Dispose()
    {
        var failures = new List<Exception>();
        try
        {
            IdentityTestCleanup.DeleteAll(_ids.Select(IdentityTestCleanup.KeyNameFor));
        }
        catch (Exception e)
        {
            failures.Add(e);
        }

        // Keys this class made under names the production cleanup must refuse to touch.
        foreach (var name in _oddKeyNames)
        {
            try
            {
                if (CngKey.Exists(name, CngProvider.MicrosoftSoftwareKeyStorageProvider, CngKeyOpenOptions.UserKey))
                {
                    using var key = CngKey.Open(name, CngProvider.MicrosoftSoftwareKeyStorageProvider, CngKeyOpenOptions.UserKey);
                    key.Delete();
                }
            }
            catch (Exception e)
            {
                failures.Add(e);
            }
        }

        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }

        var parent = Path.GetDirectoryName(_root)!;
        if (Directory.Exists(parent) && !Directory.EnumerateFileSystemEntries(parent).Any())
        {
            Directory.Delete(parent);
        }

        if (failures.Count > 0)
        {
            throw new AggregateException(failures);
        }
    }

    private static bool KeyExists(string name) =>
        CngKey.Exists(name, CngProvider.MicrosoftSoftwareKeyStorageProvider, CngKeyOpenOptions.UserKey);

    private string MakeLeftoverIdentity(TimeSpan age)
    {
        var directory = Path.Combine(_root, Guid.NewGuid().ToString("N"));
        string id;
        using (var identity = DeviceIdentity.LoadOrCreate(directory))
        {
            id = identity.DeviceId;
        }

        _ids.Add(id);
        Directory.SetLastWriteTimeUtc(directory, DateTime.UtcNow - age);
        return id;
    }

    [Fact]
    public void Sweep_RemovesTheKeyAndTheDirectoryOfAnOldLeftover()
    {
        var id = MakeLeftoverIdentity(age: TimeSpan.FromHours(1));
        Assert.True(KeyExists(IdentityTestCleanup.KeyNameFor(id)));

        var removed = IdentityTestCleanup.Sweep(_root, IdentityTestCleanup.LeftoverAge);

        Assert.Equal(1, removed);
        Assert.False(KeyExists(IdentityTestCleanup.KeyNameFor(id)), "the leftover's key was not deleted");
        Assert.Empty(Directory.Exists(_root) ? Directory.GetDirectories(_root) : []);
    }

    [Fact]
    public void Sweep_LeavesARecentDirectoryAlone_ItMayBelongToARunInProgress()
    {
        var id = MakeLeftoverIdentity(age: TimeSpan.FromSeconds(5));

        var removed = IdentityTestCleanup.Sweep(_root, IdentityTestCleanup.LeftoverAge);

        Assert.Equal(0, removed);
        Assert.True(KeyExists(IdentityTestCleanup.KeyNameFor(id)));
        Assert.Single(Directory.GetDirectories(_root));
    }

    [Fact]
    public void Sweep_DeletesOnlyKeysWhoseNameIsOfTheExactDeviceForm()
    {
        // A key named like a device key but not with 32 lowercase hex characters, and an id file
        // that names it. Whatever a file on disk says, only the exact form may be deleted.
        var almost = new string('a', 31);
        var oddName = IdentityTestCleanup.KeyNameFor(almost);
        _oddKeyNames.Add(oddName);
        using (CngKey.Create(CngAlgorithm.ECDsaP256, oddName, new CngKeyCreationParameters
        {
            Provider = CngProvider.MicrosoftSoftwareKeyStorageProvider,
            ExportPolicy = CngExportPolicies.None,
        }))
        {
        }

        var directory = Path.Combine(_root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, DeviceIdentity.IdFileName), almost);
        Directory.SetLastWriteTimeUtc(directory, DateTime.UtcNow - TimeSpan.FromHours(1));

        IdentityTestCleanup.Sweep(_root, IdentityTestCleanup.LeftoverAge);

        Assert.True(KeyExists(oddName), "a key that is not of the exact device form was deleted");
    }

    [Theory]
    [InlineData("Promin-Device-abc")]
    [InlineData("Promin-Device-0123456789ABCDEF0123456789ABCDEF")]
    [InlineData("Promin-Device-0123456789abcdef0123456789abcdef0")]
    [InlineData("promin-device-0123456789abcdef0123456789abcdef")]
    [InlineData("0123456789abcdef0123456789abcdef")]
    [InlineData("..\\Promin-Device-0123456789abcdef0123456789abcdef")]
    [InlineData("")]
    public void DeleteKeyNamed_RefusesAnyNameThatIsNotExactlyADeviceKeyName(string name)
    {
        Assert.Throws<ArgumentException>(() => IdentityTestCleanup.DeleteKeyNamed(name));
    }

    [Fact]
    public void DeleteAll_DeletesTheRest_WhenOneCannotBeDeleted()
    {
        var id = MakeLeftoverIdentity(age: TimeSpan.Zero);

        var thrown = Assert.Throws<AggregateException>(() => IdentityTestCleanup.DeleteAll(
            ["not a device key name", IdentityTestCleanup.KeyNameFor(id)]));

        Assert.Single(thrown.InnerExceptions);
        Assert.False(KeyExists(IdentityTestCleanup.KeyNameFor(id)), "one failed delete skipped the rest");
    }

    [Fact]
    public void DeleteAll_ToleratesAKeyThatIsAlreadyGone()
    {
        var gone = IdentityTestCleanup.KeyNameFor(Guid.NewGuid().ToString("N"));

        IdentityTestCleanup.DeleteAll([gone, gone]);
    }
}
