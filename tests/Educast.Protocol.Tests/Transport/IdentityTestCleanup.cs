using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;

namespace Educast.Protocol.Tests.Transport;

/// <summary>
/// Clean-up for tests that create real, persisted keys in the current Windows user's key store.
/// <para>
/// The rule these helpers keep: a key is deleted only by an exact name of the form
/// <c>Educast-Device-</c> plus 32 lowercase hex characters, and that name comes from an id a test
/// recorded when it got it, or from an id read out of one specific leftover test directory. Nothing
/// here ever enumerates the key store and deletes by pattern, and nothing touches a file in the
/// key store's own folder.
/// </para>
/// </summary>
internal static partial class IdentityTestCleanup
{
    /// <summary>A leftover directory younger than this might belong to a test run still in progress.</summary>
    public static readonly TimeSpan LeftoverAge = TimeSpan.FromMinutes(10);

    private static readonly Lock SweepGate = new();
    private static bool _swept;

    [GeneratedRegex("^[0-9a-f]{32}$")]
    private static partial Regex DeviceIdForm();

    [GeneratedRegex("^Educast-Device-[0-9a-f]{32}$")]
    private static partial Regex KeyNameForm();

    public static string TempRoot(string leaf) => Path.Combine(Path.GetTempPath(), leaf);

    public static string KeyNameFor(string deviceId) => $"Educast-Device-{deviceId}";

    public static bool IsDeviceId(string? text) => text is not null && DeviceIdForm().IsMatch(text);

    /// <summary>
    /// Deletes the named key if it exists; "already gone" is fine. Refuses (throws) any name that is
    /// not exactly <c>Educast-Device-</c> plus 32 lowercase hex characters.
    /// </summary>
    public static void DeleteKeyNamed(string name)
    {
        if (!KeyNameForm().IsMatch(name))
        {
            throw new ArgumentException($"'{name}' is not the name of a device key; refusing to delete it.", nameof(name));
        }

        if (CngKey.Exists(name, CngProvider.MicrosoftSoftwareKeyStorageProvider, CngKeyOpenOptions.UserKey))
        {
            using var key = CngKey.Open(name, CngProvider.MicrosoftSoftwareKeyStorageProvider, CngKeyOpenOptions.UserKey);
            key.Delete();
        }
    }

    /// <summary>
    /// Deletes every name in <paramref name="names"/>, whatever happens to the others, and throws one
    /// <see cref="AggregateException"/> at the end if any could not be deleted.
    /// </summary>
    public static void DeleteAll(IEnumerable<string> names)
    {
        var failures = new List<Exception>();
        foreach (var name in names.Distinct())
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

        if (failures.Count > 0)
        {
            throw new AggregateException("A test key could not be deleted from the key store.", failures);
        }
    }

    /// <summary>The device ids still readable from the files under one directory.</summary>
    public static IEnumerable<string> IdsOnDisk(string directory)
    {
        foreach (var file in Directory.GetFiles(directory, "*", SearchOption.AllDirectories))
        {
            string? text = null;
            try
            {
                text = File.ReadAllText(file).Trim();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }

            if (IsDeviceId(text))
            {
                yield return text!;
            }

            string? name = null;
            try
            {
                using var certificate = X509CertificateLoader.LoadCertificateFromFile(file);
                name = certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false);
            }
            catch (Exception e) when (e is CryptographicException or IOException or UnauthorizedAccessException)
            {
            }

            if (IsDeviceId(name))
            {
                yield return name!;
            }
        }
    }

    /// <summary>
    /// Once per test process: sweeps the leftovers of an earlier run that was killed before its
    /// teardown ran (see <see cref="Sweep"/>), in the two directories the identity tests use.
    /// </summary>
    public static void SweepOnce()
    {
        lock (SweepGate)
        {
            if (_swept)
            {
                return;
            }

            _swept = true;
        }

        Sweep(TempRoot("educast-identity-tests"), LeftoverAge);
        Sweep(TempRoot("educast-identity-transport-tests"), LeftoverAge);
    }

    /// <summary>
    /// For each directory directly under <paramref name="root"/> that was last written more than
    /// <paramref name="olderThan"/> ago: reads any device id still on disk in it, deletes the key
    /// with that exact name, then deletes the directory. A directory whose key could not be deleted
    /// is kept, so its ids can be tried again next time. Best effort: it never throws.
    /// </summary>
    /// <returns>The number of directories removed.</returns>
    public static int Sweep(string root, TimeSpan olderThan)
    {
        if (!Directory.Exists(root))
        {
            return 0;
        }

        var removed = 0;
        foreach (var directory in Directory.GetDirectories(root))
        {
            try
            {
                if (DateTime.UtcNow - Directory.GetLastWriteTimeUtc(directory) < olderThan)
                {
                    continue;
                }

                DeleteAll(IdsOnDisk(directory).Select(KeyNameFor));
                Directory.Delete(directory, recursive: true);
                removed++;
            }
            catch (Exception)
            {
                // Left for the next sweep.
            }
        }

        try
        {
            if (!Directory.EnumerateFileSystemEntries(root).Any())
            {
                Directory.Delete(root);
            }
        }
        catch (IOException)
        {
        }

        return removed;
    }
}
