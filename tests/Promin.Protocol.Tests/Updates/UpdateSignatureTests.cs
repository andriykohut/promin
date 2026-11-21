using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Promin.Protocol.Updates;

namespace Promin.Protocol.Tests.Updates;

public sealed class UpdateSignatureTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("promin-signature-").FullName;
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public void Dispose()
    {
        _key.Dispose();
        Directory.Delete(_directory, recursive: true);
    }

    private static ReleaseVersion Parse(string text) =>
        ReleaseVersion.TryParse(text, out var version) ? version : throw new FormatException(text);

    private string Setup(params byte[] bytes)
    {
        var path = Path.Combine(_directory, "Promin-Agent-Setup.exe");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private byte[] Sign(string path, string version) =>
        _key.SignData(UpdateSignature.SignedBytes(Parse(version), SHA256.HashData(File.ReadAllBytes(path))), HashAlgorithmName.SHA256);

    private byte[] PublicKey => _key.ExportSubjectPublicKeyInfo();

    [Fact]
    public void HoldsForTheFileItSigned()
    {
        var setup = Setup(1, 2, 3);
        Assert.True(UpdateSignature.HoldsFor(setup, Parse("0.4.0"), Sign(setup, "0.4.0"), PublicKey));
    }

    [Fact]
    public void FailsForAChangedByte()
    {
        var setup = Setup(1, 2, 3);
        var signature = Sign(setup, "0.4.0");
        File.WriteAllBytes(setup, [1, 2, 4]);
        Assert.False(UpdateSignature.HoldsFor(setup, Parse("0.4.0"), signature, PublicKey));
    }

    [Fact]
    public void FailsForAnotherVersion()
    {
        var setup = Setup(1, 2, 3);
        Assert.False(UpdateSignature.HoldsFor(setup, Parse("0.4.1"), Sign(setup, "0.4.0"), PublicKey));
    }

    [Fact]
    public void FailsForAnotherKey()
    {
        var setup = Setup(1, 2, 3);
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Assert.False(UpdateSignature.HoldsFor(setup, Parse("0.4.0"), Sign(setup, "0.4.0"), other.ExportSubjectPublicKeyInfo()));
    }

    [Fact]
    public void FailsForGarbage()
    {
        var setup = Setup(1, 2, 3);
        Assert.False(UpdateSignature.HoldsFor(setup, Parse("0.4.0"), [1, 2, 3], PublicKey));
        Assert.False(UpdateSignature.HoldsFor(setup, Parse("0.4.0"), Sign(setup, "0.4.0"), []));
        Assert.False(UpdateSignature.HoldsFor(setup, Parse("0.4.0"), Sign(setup, "0.4.0"), [1, 2, 3]));
    }

    [Fact]
    public void FailsForAMissingFile()
    {
        var setup = Setup(1, 2, 3);
        var signature = Sign(setup, "0.4.0");
        File.Delete(setup);
        Assert.False(UpdateSignature.HoldsFor(setup, Parse("0.4.0"), signature, PublicKey));
    }

    [Fact]
    public void SignedBytesAreTheDocumentedText()
    {
        var text = Encoding.UTF8.GetString(UpdateSignature.SignedBytes(Parse("0.4.0"), Enumerable.Repeat((byte)0xAA, 32).ToArray()));
        Assert.Equal("promin-setup\n0.4.0\n" + string.Concat(Enumerable.Repeat("aa", 32)), text);
    }

    [Fact]
    public void SigFileRoundTrips()
    {
        var path = Path.Combine(_directory, "Promin-Agent-Setup.exe" + SetupSignature.Extension);
        var written = new SetupSignature("0.4.0", [1, 2, 3]);
        written.Write(path);

        var read = SetupSignature.Read(path);
        Assert.NotNull(read);
        Assert.Equal(written.Version, read.Version);
        Assert.Equal(written.Signature, read.Signature);

        using var json = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal(["version", "signature"], json.RootElement.EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public void AnUnreadableSigFileReadsNull()
    {
        var path = Path.Combine(_directory, "x.sig");
        Assert.Null(SetupSignature.Read(path));
        File.WriteAllText(path, "not json");
        Assert.Null(SetupSignature.Read(path));
        File.WriteAllText(path, """{"version":"0.4.0"}""");
        Assert.Null(SetupSignature.Read(path));
    }

    [Fact]
    public void ProjectKeyIsAP256Key()
    {
        Assert.SkipWhen(UpdateSignature.ProjectPublicKey.IsEmpty, "The project's public key is not in yet: the owner runs installer/new-signing-key.ps1.");
        using var key = ECDsa.Create();
        key.ImportSubjectPublicKeyInfo(UpdateSignature.ProjectPublicKey.Span, out _);
        Assert.Equal(256, key.KeySize);
    }
}
