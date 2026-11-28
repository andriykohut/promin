using Promin.Master.Core.Updates;

namespace Promin.Master.Core.Tests.Updates;

public class ReleaseJsonTests
{
    private const string Download = "https://github.com/andriykohut/promin/releases/download/v0.3.0/";

    /// <summary>GitHub's answer for v0.3.0, trimmed to the fields read, with the <c>.sig</c> assets a signed release adds.</summary>
    private static string Json(string tag = "v0.3.0", bool withSigs = true)
    {
        List<string> names = ["Promin-Agent-Setup.exe", "Promin-Console-Setup.exe"];
        if (withSigs)
        {
            names.AddRange(["Promin-Agent-Setup.exe.sig", "Promin-Console-Setup.exe.sig"]);
        }

        var assets = string.Join(',', names.Select(n => $$"""{"name":"{{n}}","browser_download_url":"{{Download}}{{n}}","size":120}"""));
        return $$"""{"tag_name":"{{tag}}","prerelease":false,"assets":[{{assets}}]}""";
    }

    [Fact]
    public void ReadsTheFourAssets()
    {
        var release = ReleaseJson.Parse(Json());

        Assert.NotNull(release);
        Assert.Equal("0.3.0", release.Version.ToString());
        Assert.Equal(new Uri(Download + "Promin-Agent-Setup.exe"), release.AgentSetup);
        Assert.Equal(new Uri(Download + "Promin-Agent-Setup.exe.sig"), release.AgentSignature);
        Assert.Equal(new Uri(Download + "Promin-Console-Setup.exe"), release.ConsoleSetup);
        Assert.Equal(new Uri(Download + "Promin-Console-Setup.exe.sig"), release.ConsoleSignature);
    }

    [Fact]
    public void AReleaseWithoutSignatures_IsNull() => Assert.Null(ReleaseJson.Parse(Json(withSigs: false)));

    [Fact]
    public void AnUnreadableTag_IsNull() => Assert.Null(ReleaseJson.Parse(Json(tag: "latest")));

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{"tag_name":"v0.3.0"}""")]
    [InlineData("""{"tag_name":"v0.3.0","assets":[{"name":"Promin-Agent-Setup.exe","browser_download_url":"http://x/a"}]}""")]
    public void AnythingElse_IsNull(string json) => Assert.Null(ReleaseJson.Parse(json));

    [Fact]
    public void AnAssetNotOverHttps_IsNull() => Assert.Null(ReleaseJson.Parse(Json().Replace("https://", "http://", StringComparison.Ordinal)));
}
