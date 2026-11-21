using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Promin.Master.Core.Tests.TestDoubles;
using Promin.Master.Core.Updates;
using Promin.Protocol.Updates;

namespace Promin.Master.Core.Tests.Updates;

/// <summary>Downloading a release's setups into the cache, with setups and signatures served by a stub and signed by a test key.</summary>
public sealed class UpdateCacheTests : IDisposable
{
    private const string Download = "https://github.com/andriykohut/promin/releases/download/v0.4.0/";

    private readonly string _directory = Path.Combine(Directory.CreateTempSubdirectory("promin-cache-").FullName, "Updates");
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly StubHttpHandler _http = new();
    private readonly byte[] _agent = RandomNumberGenerator.GetBytes(5000);
    private readonly byte[] _console = RandomNumberGenerator.GetBytes(4000);

    public UpdateCacheTests()
    {
        Serve("Promin-Agent-Setup.exe", _agent);
        Serve("Promin-Console-Setup.exe", _console);
    }

    public void Dispose()
    {
        _key.Dispose();
        Directory.Delete(Path.GetDirectoryName(_directory)!, recursive: true);
    }

    private static ReleaseVersion Parse(string text) =>
        ReleaseVersion.TryParse(text, out var version) ? version : throw new FormatException(text);

    private static Release Release => new(
        Parse("0.4.0"),
        new Uri(Download + "Promin-Agent-Setup.exe"),
        new Uri(Download + "Promin-Agent-Setup.exe.sig"),
        new Uri(Download + "Promin-Console-Setup.exe"),
        new Uri(Download + "Promin-Console-Setup.exe.sig"));

    private UpdateCache Cache() => new(_directory, new HttpClient(_http), _key.ExportSubjectPublicKeyInfo(), idleLimit: TimeSpan.FromMilliseconds(200));

    /// <summary>Serves a setup and its .sig, which claims <paramref name="sigVersion"/> and is signed as <paramref name="signedAs"/>.</summary>
    private void Serve(string name, byte[] bytes, string sigVersion = "0.4.0", string signedAs = "0.4.0", string? signedFor = null)
    {
        _http.Serve(Download + name, bytes);
        var signature = _key.SignData(UpdateSignature.SignedBytes(signedFor ?? name, Parse(signedAs), SHA256.HashData(bytes)), HashAlgorithmName.SHA256);
        _http.Serve(Download + name + ".sig", JsonSerializer.SerializeToUtf8Bytes(new SetupSignature(sigVersion, signature)));
    }

    private int Requests => _http.Requests.Count;

    [Fact]
    public async Task DownloadsAndChecksBoth()
    {
        var set = await Cache().GetAsync(Release, withConsole: true, TestContext.Current.CancellationToken);

        Assert.NotNull(set);
        Assert.Equal(_agent, File.ReadAllBytes(set.Agent.SetupPath));
        Assert.Equal(_console, File.ReadAllBytes(set.Console!.SetupPath));
        Assert.Equal(Parse("0.4.0"), set.Agent.Version);
        Assert.StartsWith(Path.Combine(_directory, "0.4.0"), set.Agent.SetupPath, StringComparison.OrdinalIgnoreCase);
        Assert.True(UpdateSignature.HoldsFor(set.Agent.SetupPath, ReleaseJson.AgentSetupName, set.Agent.Version, set.Agent.Signature, _key.ExportSubjectPublicKeyInfo()));
        Assert.All(_http.Requests, r => Assert.Equal("Promin", r.Headers.UserAgent.ToString()));
    }

    [Fact]
    public async Task WhenTheConsoleIsCurrent_OnlyTheAgentIsFetched()
    {
        var set = await Cache().GetAsync(Release, withConsole: false, TestContext.Current.CancellationToken);

        Assert.NotNull(set);
        Assert.Null(set.Console);
        Assert.DoesNotContain(_http.Requests, r => r.RequestUri!.ToString().Contains("Console", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ABadSignature_LeavesNothing()
    {
        Serve("Promin-Agent-Setup.exe", _agent, signedAs: "0.4.1");

        Assert.Null(await Cache().GetAsync(Release, withConsole: false, TestContext.Current.CancellationToken));
        Assert.False(Directory.Exists(Path.Combine(_directory, "0.4.0")));
    }

    [Fact]
    public async Task TheConsoleSetupServedAsTheAgentSetup_LeavesNothing()
    {
        Serve("Promin-Agent-Setup.exe", _console, signedFor: "Promin-Console-Setup.exe");

        Assert.Null(await Cache().GetAsync(Release, withConsole: false, TestContext.Current.CancellationToken));
        Assert.False(Directory.Exists(Path.Combine(_directory, "0.4.0")));
    }

    [Fact]
    public async Task ASigForAnotherVersion_LeavesNothing()
    {
        Serve("Promin-Console-Setup.exe", _console, sigVersion: "0.4.1", signedAs: "0.4.1");

        Assert.Null(await Cache().GetAsync(Release, withConsole: true, TestContext.Current.CancellationToken));
        Assert.False(Directory.Exists(Path.Combine(_directory, "0.4.0")));
    }

    [Fact]
    public async Task AFailedDownload_LeavesNothing()
    {
        _http.Responses[Download + "Promin-Agent-Setup.exe"] = (HttpStatusCode.InternalServerError, []);

        Assert.Null(await Cache().GetAsync(Release, withConsole: false, TestContext.Current.CancellationToken));
        Assert.False(Directory.Exists(Path.Combine(_directory, "0.4.0")));
    }

    [Fact]
    public async Task NoNetwork_IsNull()
    {
        _http.Throw = new HttpRequestException("no route to host");
        Assert.Null(await Cache().GetAsync(Release, withConsole: false, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ATimedOutRequest_IsNull()
    {
        // What HttpClient throws when its own Timeout passes.
        _http.Throw = new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout.");
        Assert.Null(await Cache().GetAsync(Release, withConsole: false, TestContext.Current.CancellationToken));
    }

    [Fact(Timeout = 20000)]
    public async Task AStalledDownload_IsGivenUp()
    {
        _http.Stalled.Add(Download + "Promin-Agent-Setup.exe");

        Assert.Null(await Cache().GetAsync(Release, withConsole: false, TestContext.Current.CancellationToken));
        Assert.False(Directory.Exists(Path.Combine(_directory, "0.4.0")));
    }

    [Fact]
    public async Task CheckedFiles_AreReused()
    {
        await Cache().GetAsync(Release, withConsole: true, TestContext.Current.CancellationToken);
        var before = Requests;

        Assert.NotNull(await Cache().GetAsync(Release, withConsole: true, TestContext.Current.CancellationToken));
        Assert.Equal(before, Requests);
    }

    [Fact]
    public async Task OnlyTheNewestIsKept()
    {
        var older = Path.Combine(_directory, "0.3.0");
        Directory.CreateDirectory(older);
        File.WriteAllBytes(Path.Combine(older, "Promin-Agent-Setup.exe"), [1]);
        new PendingUpdate("0.4.0", []).Write(Path.Combine(_directory, "pending.json"));

        await Cache().GetAsync(Release, withConsole: false, TestContext.Current.CancellationToken);

        Assert.False(Directory.Exists(older));
        Assert.True(File.Exists(Path.Combine(_directory, "pending.json")));
    }

    [Fact]
    public async Task CachedAgent_FindsACheckedCopy()
    {
        await Cache().GetAsync(Release, withConsole: false, TestContext.Current.CancellationToken);

        var cached = Cache().CachedAgent(Parse("0.4.0"));
        Assert.NotNull(cached);
        Assert.Equal(_agent, File.ReadAllBytes(cached.SetupPath));
        Assert.Null(Cache().CachedAgent(Parse("0.4.1")));
    }

    [Fact]
    public async Task CachedAgent_IgnoresATamperedCopy()
    {
        var set = await Cache().GetAsync(Release, withConsole: false, TestContext.Current.CancellationToken);
        File.WriteAllBytes(set!.Agent.SetupPath, [1, 2, 3]);

        Assert.Null(Cache().CachedAgent(Parse("0.4.0")));
    }
}
