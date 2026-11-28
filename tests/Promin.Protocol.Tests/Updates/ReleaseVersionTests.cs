using Promin.Protocol.Updates;

namespace Promin.Protocol.Tests.Updates;

public class ReleaseVersionTests
{
    private static ReleaseVersion Parse(string text) =>
        ReleaseVersion.TryParse(text, out var version) ? version : throw new FormatException(text);

    [Theory]
    [InlineData("0.4.0", "0.4.0")]
    [InlineData("v0.4.0", "0.4.0")]
    [InlineData("0.4.0-beta.1", "0.4.0-beta.1")]
    [InlineData("0.3.0-pr65", "0.3.0-pr65")]
    [InlineData("0.4.0+3b97a8b", "0.4.0")]
    [InlineData("0.4.0-beta.1+3b97a8b", "0.4.0-beta.1")]
    public void Parses(string text, string shown)
    {
        Assert.True(ReleaseVersion.TryParse(text, out var version));
        Assert.Equal(shown, version.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0.4")]
    [InlineData("0.4.0.1")]
    [InlineData("a.b.c")]
    [InlineData("0.4.0-")]
    [InlineData("0.4.0-beta..1")]
    [InlineData("01.2.3")]
    [InlineData("0.4.0-beta.01")]
    [InlineData("-1.2.3")]
    public void RefusesWhatIsNotAVersion(string? text) => Assert.False(ReleaseVersion.TryParse(text, out _));

    /// <summary>SemVer 2.0 precedence, each version lower than the next.</summary>
    [Fact]
    public void OrdersAsSemVer()
    {
        string[] ascending =
        [
            "0.3.0-beta.1", "0.3.0-beta.2", "0.3.0-beta.10", "0.3.0-pr65", "0.3.0", "0.3.1",
            "0.4.0-alpha", "0.4.0-alpha.1", "0.4.0", "1.0.0",
        ];
        var parsed = ascending.Select(Parse).ToList();
        for (var i = 1; i < parsed.Count; i++)
        {
            Assert.True(parsed[i - 1] < parsed[i], $"{parsed[i - 1]} < {parsed[i]}");
            Assert.True(parsed[i] > parsed[i - 1], $"{parsed[i]} > {parsed[i - 1]}");
        }
    }

    [Fact]
    public void EqualVersionsAreEqual()
    {
        Assert.Equal(Parse("0.4.0-beta.1"), Parse("v0.4.0-beta.1+abc"));
        Assert.Equal(Parse("0.4.0-beta.1").GetHashCode(), Parse("v0.4.0-beta.1+abc").GetHashCode());
        Assert.False(Parse("0.4.0") < Parse("0.4.0"));
        Assert.True(Parse("0.4.0") <= Parse("0.4.0"));
        Assert.True(Parse("0.4.0") >= Parse("0.4.0"));
    }

    [Fact]
    public void OfReadsTheInformationalVersion() => Assert.NotNull(ReleaseVersion.Of(typeof(ReleaseVersion).Assembly));
}
