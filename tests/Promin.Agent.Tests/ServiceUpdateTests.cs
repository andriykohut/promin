using System.Security.Cryptography;
using Promin.Protocol.Updates;

namespace Promin.Agent.Tests;

/// <summary>The service's check of an accepted update before it runs the setup, with setups signed by a test key.</summary>
public sealed class ServiceUpdateTests : IDisposable
{
    private readonly TempDirectory _dir = new();
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly List<string> _started = [];
    private readonly List<string> _log = [];

    public void Dispose()
    {
        _key.Dispose();
        _dir.Dispose();
    }

    private string Folder => Path.Combine(_dir.Path, "update");

    private string Setup => Path.Combine(Folder, UpdateLink.SetupName);

    private static ReleaseVersion Parse(string text) =>
        ReleaseVersion.TryParse(text, out var version) ? version : throw new FormatException(text);

    /// <summary>Puts a setup in the folder with a .sig for <paramref name="version"/>, signed as <paramref name="signedAs"/>.</summary>
    private void Accepted(string version, string? signedAs = null)
    {
        Directory.CreateDirectory(Folder);
        byte[] bytes = [1, 2, 3, 4];
        File.WriteAllBytes(Setup, bytes);
        var signature = _key.SignData(
            UpdateSignature.SignedBytes(Parse(signedAs ?? version), SHA256.HashData(bytes)), HashAlgorithmName.SHA256);
        new SetupSignature(version, signature).Write(Setup + SetupSignature.Extension);
    }

    private bool TryStart(bool starts = true) =>
        ServiceUpdate.TryStart(Folder, Parse("0.3.0"), _key.ExportSubjectPublicKeyInfo(), path => { _started.Add(path); return starts; }, _log.Add);

    [Fact]
    public void StartsAGoodSetup()
    {
        Accepted("0.4.0");
        Assert.True(TryStart());
        Assert.Equal([Setup], _started);
    }

    [Fact]
    public void RefusesABadSignature_AndClearsTheFolder()
    {
        Accepted("0.4.0", signedAs: "0.4.1");
        Assert.False(TryStart());
        Assert.Empty(_started);
        Assert.Empty(Directory.GetFileSystemEntries(Folder));
    }

    [Fact]
    public void RefusesANotNewerVersion_AndClearsTheFolder()
    {
        Accepted("0.3.0");
        Assert.False(TryStart());
        Assert.Empty(_started);
        Assert.Empty(Directory.GetFileSystemEntries(Folder));
    }

    [Fact]
    public void RefusesAMissingSig()
    {
        Accepted("0.4.0");
        File.Delete(Setup + SetupSignature.Extension);
        Assert.False(TryStart());
        Assert.Empty(_started);
        Assert.Empty(Directory.GetFileSystemEntries(Folder));
    }

    [Fact]
    public void RefusesAMissingFolder() => Assert.False(TryStart());

    [Fact]
    public void ReportsASetupThatCannotStart()
    {
        Accepted("0.4.0");
        Assert.False(TryStart(starts: false));
        Assert.Equal([Setup], _started);
    }
}
