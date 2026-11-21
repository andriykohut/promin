using System.Diagnostics;
using System.Security.Cryptography;
using Promin.Protocol.Updates;

namespace Promin.Protocol.Tests.Updates;

/// <summary>
/// <c>installer/sign-setup.ps1</c>, run as the release workflow runs it, makes signatures the C# check accepts. Needs
/// PowerShell 7 (<c>pwsh</c>) on the PATH, as the CI runner and the build machines have it.
/// </summary>
public sealed class SignSetupScriptTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("promin-sign-script-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static string Script()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Promin.slnx")))
            {
                return Path.Combine(dir.FullName, "installer", "sign-setup.ps1");
            }
        }

        throw new InvalidOperationException("The repository root was not found above the test's folder.");
    }

    private static async Task<int> RunAsync(CancellationToken ct, string? key, params string[] arguments)
    {
        var start = new ProcessStartInfo("pwsh") { RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var argument in (string[])["-NoProfile", "-NonInteractive", "-File", Script(), .. arguments])
        {
            start.ArgumentList.Add(argument);
        }

        start.Environment["PROMIN_SIGNING_KEY"] = key ?? string.Empty;
        using var process = Process.Start(start)!;
        await Task.WhenAll(process.StandardOutput.ReadToEndAsync(), process.StandardError.ReadToEndAsync());
        await process.WaitForExitAsync(ct);
        return process.ExitCode;
    }

    [Fact(Timeout = 60000)]
    public async Task SignsWhatTheAgentAccepts()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var setup = Path.Combine(_directory, "Promin-Agent-Setup.exe");
        File.WriteAllBytes(setup, RandomNumberGenerator.GetBytes(4096));

        Assert.Equal(0, await RunAsync(TestContext.Current.CancellationToken, Convert.ToBase64String(key.ExportPkcs8PrivateKey()), "-Version", "0.4.0", "-Setups", setup));

        var sig = SetupSignature.Read(setup + SetupSignature.Extension);
        Assert.NotNull(sig);
        Assert.Equal("0.4.0", sig.Version);
        Assert.True(ReleaseVersion.TryParse(sig.Version, out var version));
        Assert.True(UpdateSignature.HoldsFor(setup, "Promin-Agent-Setup.exe", version, sig.Signature, key.ExportSubjectPublicKeyInfo()));
    }

    [Fact(Timeout = 60000)]
    public async Task SignsEachSetupForItsOwnName()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var setup = Path.Combine(_directory, "Promin-Console-Setup.exe");
        File.WriteAllBytes(setup, [1, 2, 3]);

        Assert.Equal(0, await RunAsync(TestContext.Current.CancellationToken, Convert.ToBase64String(key.ExportPkcs8PrivateKey()), "-Version", "0.4.0", "-Setups", setup));

        var sig = SetupSignature.Read(setup + SetupSignature.Extension)!;
        Assert.True(ReleaseVersion.TryParse(sig.Version, out var version));
        Assert.True(UpdateSignature.HoldsFor(setup, "Promin-Console-Setup.exe", version, sig.Signature, key.ExportSubjectPublicKeyInfo()));
        Assert.False(UpdateSignature.HoldsFor(setup, "Promin-Agent-Setup.exe", version, sig.Signature, key.ExportSubjectPublicKeyInfo()));
    }

    [Fact(Timeout = 60000)]
    public async Task RefusesWithoutAKey()
    {
        var setup = Path.Combine(_directory, "Promin-Agent-Setup.exe");
        File.WriteAllBytes(setup, [1, 2, 3]);

        Assert.NotEqual(0, await RunAsync(TestContext.Current.CancellationToken, null, "-Version", "0.4.0", "-Setups", setup));
        Assert.False(File.Exists(setup + SetupSignature.Extension));
    }
}
