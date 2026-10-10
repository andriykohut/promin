using Promin.Protocol.Transport;

namespace Promin.Agent.Tests;

/// <summary>
/// The command-line forms that do one thing and exit, run in this process against a temporary folder. (Run for
/// real they would write under the real ProgramData and LocalAppData.)
/// </summary>
public class AgentCommandsTests
{
    private static (int ExitCode, string Output) Run(AgentPaths paths, params string[] args)
    {
        var output = new StringWriter();
        var exitCode = AgentCommands.Execute(AgentCommandLine.Parse(args), paths, output);
        return (exitCode, output.ToString());
    }

    // ---- --set-room-code -------------------------------------------------------------------------

    [Fact]
    public void SetRoomCode_StoresTheCode_SaysOneLine_AndExitsZero()
    {
        using var temp = new TempDirectory();

        var (exitCode, output) = Run(temp.Paths, "--set-room-code", "482913");

        Assert.Equal(0, exitCode);
        Assert.Equal("482913", new AgentSettings(temp.Paths.RoomCodeFile).Load());
        Assert.Single(output.TrimEnd().Split('\n'));
        Assert.Contains(temp.Paths.RoomCodeFile, output);
    }

    [Theory]
    [InlineData("482 913")]
    [InlineData("482-913")]
    public void SetRoomCode_StoresThePlainSixDigits_WhateverTheGrouping(string typed)
    {
        using var temp = new TempDirectory();

        var (exitCode, _) = Run(temp.Paths, "--set-room-code", typed);

        Assert.Equal(0, exitCode);
        Assert.Equal("482913", new AgentSettings(temp.Paths.RoomCodeFile).Load());
    }

    [Theory]
    [InlineData("48291")]
    [InlineData("4829133")]
    [InlineData("48291a")]
    [InlineData("")]
    public void SetRoomCode_RefusesACodeThatIsNotSixDigits_WithExitCodeTwo_AndWritesNothing(string typed)
    {
        using var temp = new TempDirectory();

        var (exitCode, output) = Run(temp.Paths, "--set-room-code", typed);

        Assert.Equal(2, exitCode);
        Assert.Contains("6 digits", output);
        Assert.False(File.Exists(temp.Paths.RoomCodeFile));
        Assert.False(Directory.Exists(Path.GetDirectoryName(temp.Paths.RoomCodeFile)));
    }

    [Fact]
    public void SetRoomCode_KeepsTheOldCode_WhenTheNewOneIsRefused()
    {
        using var temp = new TempDirectory();
        Run(temp.Paths, "--set-room-code", "111111");

        Run(temp.Paths, "--set-room-code", "12");

        Assert.Equal("111111", new AgentSettings(temp.Paths.RoomCodeFile).Load());
    }

    [Fact]
    public void SetRoomCode_SaysWhenTheFileCannotBeWritten_WithExitCodeFive()
    {
        using var temp = new TempDirectory();
        File.WriteAllText(temp.File("a-file"), "");
        var paths = new AgentPaths(temp.File("unused"), Path.Combine(temp.File("a-file"), "agent.json"));

        var (exitCode, output) = Run(paths, "--set-room-code", "482913");

        Assert.Equal(5, exitCode);
        Assert.Contains("could not be saved", output);
    }

    // ---- --reset-pairing -------------------------------------------------------------------------

    private static string OnePinnedMaster => "{\"master-1\":\"" + new string('A', 64) + "\"}";

    [Fact]
    public void ResetPairing_ForgetsThePinnedMaster_SoTheNextStartAdmitsAnyone()
    {
        using var temp = new TempDirectory();
        var paths = temp.Paths;
        Directory.CreateDirectory(paths.AgentDirectory);
        File.WriteAllText(paths.MastersFile, OnePinnedMaster);
        var before = new PeerCertificateStore();
        before.Load(paths.MastersFile);
        Assert.Equal(1, before.Count); // the next start would admit pinned peers only

        var (exitCode, output) = Run(paths, "--reset-pairing");

        Assert.Equal(0, exitCode);
        Assert.False(File.Exists(paths.MastersFile));
        var after = new PeerCertificateStore();
        after.Load(paths.MastersFile);
        Assert.Equal(0, after.Count); // the next start admits anyone again
        Assert.Single(output.TrimEnd().Split('\n'));
    }

    [Fact]
    public void ResetPairing_LeavesTheIdentityAndTheRoomCodeAlone()
    {
        using var temp = new TempDirectory();
        var paths = temp.Paths;
        Directory.CreateDirectory(paths.IdentityDirectory);
        File.WriteAllText(Path.Combine(paths.IdentityDirectory, "device-id.txt"), "x");
        File.WriteAllText(paths.MastersFile, OnePinnedMaster);
        new AgentSettings(paths.RoomCodeFile).Save("482913");

        Run(paths, "--reset-pairing");

        Assert.True(File.Exists(Path.Combine(paths.IdentityDirectory, "device-id.txt")));
        Assert.Equal("482913", new AgentSettings(paths.RoomCodeFile).Load());
    }

    [Fact]
    public void ResetPairing_WithNothingPaired_SaysSo_AndExitsZero()
    {
        using var temp = new TempDirectory();

        var (exitCode, output) = Run(temp.Paths, "--reset-pairing");

        Assert.Equal(0, exitCode);
        Assert.Contains("No console PC is paired", output);
    }

    [Fact]
    public void ResetPairing_SaysWhenTheFileCannotBeDeleted_WithExitCodeFive()
    {
        using var temp = new TempDirectory();
        var paths = temp.Paths;
        Directory.CreateDirectory(paths.AgentDirectory);
        File.WriteAllText(paths.MastersFile, OnePinnedMaster);
        using var held = new FileStream(paths.MastersFile, FileMode.Open, FileAccess.Read, FileShare.None);

        var (exitCode, output) = Run(paths, "--reset-pairing");

        Assert.Equal(5, exitCode);
        Assert.Contains("could not be forgotten", output);
    }

    // ---- Anything else --------------------------------------------------------------------------

    [Fact]
    public void BadArguments_SayWhatIsWrongAndTheUsage_AndExitWithCodeTwo()
    {
        using var temp = new TempDirectory();

        var (exitCode, output) = Run(temp.Paths, "--bogus");

        Assert.Equal(2, exitCode);
        Assert.Contains("--bogus", output);
        Assert.Contains(AgentCommandLine.Usage, output);
        Assert.False(Directory.Exists(temp.Paths.AgentDirectory));
    }

    [Fact]
    public void ARunCommand_IsNotSomethingThatExits()
    {
        using var temp = new TempDirectory();

        Assert.Throws<ArgumentException>(() =>
            AgentCommands.Execute(AgentCommandLine.Parse([]), temp.Paths, TextWriter.Null));
    }
}
