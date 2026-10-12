namespace Promin.Agent.Tests;

public class AgentCommandLineTests
{
    [Fact]
    public void NoArguments_RunTheAgent_FullScreen()
    {
        var command = AgentCommandLine.Parse([]);

        Assert.Equal(AgentMode.Run, command.Mode);
        Assert.False(command.Windowed);
    }

    [Fact]
    public void Windowed_RunsTheAgent_InAWindow()
    {
        var command = AgentCommandLine.Parse(["--windowed"]);

        Assert.Equal(AgentMode.Run, command.Mode);
        Assert.True(command.Windowed);
    }

    [Fact]
    public void SetRoomCode_CarriesTheArgumentAsTyped()
    {
        var command = AgentCommandLine.Parse(["--set-room-code", "482 913"]);

        Assert.Equal(AgentMode.SetRoomCode, command.Mode);
        Assert.Equal("482 913", command.RoomCode);
    }

    [Fact]
    public void ResetPairing_IsRecognised()
    {
        Assert.Equal(AgentMode.ResetPairing, AgentCommandLine.Parse(["--reset-pairing"]).Mode);
    }

    [Fact]
    public void TheOptionsDoNotCareAboutCase()
    {
        Assert.True(AgentCommandLine.Parse(["--WINDOWED"]).Windowed);
        Assert.Equal(AgentMode.ResetPairing, AgentCommandLine.Parse(["--Reset-Pairing"]).Mode);
    }

    [Theory]
    [InlineData("--install", AgentMode.Install)]
    [InlineData("--uninstall", AgentMode.Uninstall)]
    [InlineData("--service", AgentMode.Service)]
    public void TheServiceCommands_AreRecognised(string arg, object expected) // object: AgentMode is internal, a public test can't take it
    {
        Assert.Equal(expected, (object)AgentCommandLine.Parse([arg]).Mode);
    }

    [Fact]
    public void TheServiceCommands_DoNotCareAboutCase()
    {
        Assert.Equal(AgentMode.Install, AgentCommandLine.Parse(["--INSTALL"]).Mode);
    }

    [Theory]
    [InlineData("--install", "extra")]
    [InlineData("--service", "--windowed")]
    public void TheServiceCommands_TakeNoExtraArgument(params string[] args)
    {
        Assert.Equal(AgentMode.Invalid, AgentCommandLine.Parse(args).Mode);
    }

    [Theory]
    [InlineData("--set-room-code")]
    [InlineData("--set-room-code", "123456", "extra")]
    [InlineData("--reset-pairing", "--windowed")]
    [InlineData("--windowed", "--set-room-code", "123456")]
    [InlineData("--window")]
    [InlineData("482913")]
    public void AnythingElse_IsInvalid_AndSaysSo(params string[] args)
    {
        var command = AgentCommandLine.Parse(args);

        Assert.Equal(AgentMode.Invalid, command.Mode);
        Assert.False(string.IsNullOrWhiteSpace(command.Problem));
    }
}
