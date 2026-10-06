namespace Educast.Agent.Tests;

public class AgentSettingsTests
{
    [Fact]
    public void Load_ReturnsWhatSaveWrote()
    {
        using var temp = new TempDirectory();
        var settings = new AgentSettings(temp.Paths.RoomCodeFile); // its folder does not exist yet

        settings.Save("482913");

        Assert.Equal("482913", settings.Load());
    }

    [Fact]
    public void Save_ReplacesTheCodeThatWasThere_AndLeavesNoTemporaryFile()
    {
        using var temp = new TempDirectory();
        var settings = new AgentSettings(temp.Paths.RoomCodeFile);
        settings.Save("111111");

        settings.Save("222222");

        Assert.Equal("222222", settings.Load());
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(temp.Paths.RoomCodeFile)!));
    }

    [Fact]
    public void Load_IsNull_WhenThereIsNoFile()
    {
        using var temp = new TempDirectory();

        Assert.Null(new AgentSettings(temp.File("missing.json")).Load());
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"roomCode\":482913}")]            // a number, not a string
    [InlineData("{\"roomCode\":null}")]
    [InlineData("{\"roomCode\":\"48291\"}")]         // five digits
    [InlineData("{\"roomCode\":\"4829130\"}")]       // seven
    [InlineData("{\"roomCode\":\"48291a\"}")]
    [InlineData("{\"roomCode\":\"482 913\"}")]       // a stored code is the plain six digits
    [InlineData("{\"roomCode\":\" 482913\"}")]
    public void Load_IsNull_ForAFileThatDoesNotHoldAValidCode(string content)
    {
        using var temp = new TempDirectory();
        var path = temp.File("agent.json");
        File.WriteAllText(path, content);

        Assert.Null(new AgentSettings(path).Load());
    }

    [Fact]
    public void Load_IsNull_ForAFileItCannotRead()
    {
        using var temp = new TempDirectory();
        var path = temp.File("agent.json");
        new AgentSettings(path).Save("482913");

        using var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);

        Assert.Null(new AgentSettings(path).Load());
    }

    [Theory]
    [InlineData("48291")]
    [InlineData("482 913")]
    [InlineData("")]
    public void Save_RefusesAnythingButSixDigits(string code)
    {
        using var temp = new TempDirectory();
        var path = temp.File("agent.json");

        Assert.Throws<ArgumentException>(() => new AgentSettings(path).Save(code));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Lookup_SaysNothingIsWrong_WhenThereIsNoFile()
    {
        using var temp = new TempDirectory();

        var lookup = new AgentSettings(temp.File("missing.json")).Lookup();

        Assert.Null(lookup.Code);
        Assert.Null(lookup.Problem);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"roomCode\":\"12\"}")]
    [InlineData("{}")]
    public void Lookup_SaysTheFileIsThereAndCannotBeUsed(string content)
    {
        using var temp = new TempDirectory();
        var path = temp.File("agent.json");
        File.WriteAllText(path, content);

        var lookup = new AgentSettings(path).Lookup();

        Assert.Null(lookup.Code);
        Assert.Contains(path, lookup.Problem);
        Assert.Contains("exists but", lookup.Problem);
    }

    [Fact]
    public void Lookup_GivesTheCode_AndNoProblem_ForAGoodFile()
    {
        using var temp = new TempDirectory();
        var settings = new AgentSettings(temp.File("agent.json"));
        settings.Save("482913");

        Assert.Equal(new Educast.Protocol.Transport.RoomCodeLookup("482913"), settings.Lookup());
    }
}
