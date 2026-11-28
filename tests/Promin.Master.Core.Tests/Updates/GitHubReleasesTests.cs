using System.Net;
using System.Text;
using Promin.Master.Core.Tests.TestDoubles;
using Promin.Master.Core.Updates;
using Promin.Protocol.Updates;

namespace Promin.Master.Core.Tests.Updates;

public class GitHubReleasesTests
{
    private const string Api = "https://api.github.com/repos/andriykohut/promin/releases/";

    private static byte[] Release(string tag)
    {
        var download = $"https://github.com/andriykohut/promin/releases/download/{tag}/";
        string[] names = ["Promin-Agent-Setup.exe", "Promin-Agent-Setup.exe.sig", "Promin-Console-Setup.exe", "Promin-Console-Setup.exe.sig"];
        var assets = string.Join(',', names.Select(n => $$"""{"name":"{{n}}","browser_download_url":"{{download}}{{n}}"}"""));
        return Encoding.UTF8.GetBytes($$"""{"tag_name":"{{tag}}","assets":[{{assets}}]}""");
    }

    [Fact]
    public async Task AsksForTheLatestRelease_AsPromin()
    {
        var handler = new StubHttpHandler();
        handler.Serve(Api + "latest", Release("v0.4.0"));

        var release = await new GitHubReleases(new HttpClient(handler)).LatestAsync(TestContext.Current.CancellationToken);

        Assert.Equal("0.4.0", release?.Version.ToString());
        var request = Assert.Single(handler.Requests);
        Assert.Equal("Promin", request.Headers.UserAgent.ToString());
        Assert.Contains("application/vnd.github+json", request.Headers.Accept.ToString());
    }

    [Fact]
    public async Task AsksForAVersionByItsTag()
    {
        var handler = new StubHttpHandler();
        handler.Serve(Api + "tags/v0.4.0-beta.2", Release("v0.4.0-beta.2"));
        Assert.True(ReleaseVersion.TryParse("0.4.0-beta.2", out var version));

        var release = await new GitHubReleases(new HttpClient(handler)).ByVersionAsync(version, TestContext.Current.CancellationToken);

        Assert.Equal(version, release?.Version);
    }

    [Fact]
    public async Task NotFound_IsNull()
    {
        var handler = new StubHttpHandler();
        Assert.Null(await new GitHubReleases(new HttpClient(handler)).LatestAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AnotherStatus_IsNull()
    {
        var handler = new StubHttpHandler();
        handler.Responses[Api + "latest"] = (HttpStatusCode.Forbidden, Release("v0.4.0"));
        Assert.Null(await new GitHubReleases(new HttpClient(handler)).LatestAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Unreachable_IsNull()
    {
        var handler = new StubHttpHandler { Throw = new HttpRequestException("no route to host") };
        Assert.Null(await new GitHubReleases(new HttpClient(handler)).LatestAsync(TestContext.Current.CancellationToken));
    }
}
