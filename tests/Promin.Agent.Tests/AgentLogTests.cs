namespace Promin.Agent.Tests;

public class AgentLogTests
{
    [Fact]
    public void EachEventIsOneLine_WithATimestamp()
    {
        using var temp = new TempDirectory();
        var log = new AgentLog(temp.File("agent.log"));

        log.Write("first");
        log.Write("second");

        var lines = File.ReadAllLines(log.FilePath);
        Assert.Equal(2, lines.Length);
        Assert.Matches(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3}  first$", lines[0]);
        Assert.EndsWith("  second", lines[1]);
    }

    [Fact]
    public void ALineBreakInsideAMessage_DoesNotMakeASecondLine()
    {
        using var temp = new TempDirectory();
        var log = new AgentLog(temp.File("agent.log"));

        log.Write("one\r\ntwo\nthree");

        var line = Assert.Single(File.ReadAllLines(log.FilePath));
        Assert.EndsWith("  one two three", line);
    }

    [Fact]
    public void ItCreatesItsFolder_AndAppendsToWhatIsThere()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "a", "b", "agent.log");

        new AgentLog(path).Write("before");
        new AgentLog(path).Write("after");

        Assert.Equal(2, File.ReadAllLines(path).Length);
    }

    [Fact]
    public void WhenTheFileHasReachedItsCap_ItBecomesTheOldFile_AndANewOneBegins()
    {
        using var temp = new TempDirectory();
        var log = new AgentLog(temp.File("agent.log"), maxBytes: 200);

        for (var i = 0; i < 6; i++)
        {
            log.Write($"line {i} " + new string('x', 30));
        }

        var current = File.ReadAllLines(log.FilePath);
        var previous = File.ReadAllLines(log.FilePath + ".1");
        Assert.NotEmpty(current);
        Assert.NotEmpty(previous);
        Assert.EndsWith(new string('x', 30), current[^1]);
        Assert.Contains("line 5", current[^1]);
    }

    [Fact]
    public void Rotation_KeepsOnlyOnePreviousFile_AndTheTotalBounded()
    {
        using var temp = new TempDirectory();
        const int cap = 500;
        var log = new AgentLog(temp.File("agent.log"), maxBytes: cap);

        for (var i = 0; i < 200; i++)
        {
            log.Write($"line {i} " + new string('x', 40));
        }

        Assert.Equal(["agent.log", "agent.log.1"], Directory.GetFiles(temp.Path).Select(Path.GetFileName).Order());
        Assert.True(new FileInfo(log.FilePath).Length < cap + 100);
        Assert.True(new FileInfo(log.FilePath + ".1").Length < cap + 100);
    }

    [Fact]
    public void ANewLogObject_ContinuesAnExistingFile_AndStillRotatesIt()
    {
        using var temp = new TempDirectory();
        var path = temp.File("agent.log");
        File.WriteAllText(path, new string('y', 300));

        new AgentLog(path, maxBytes: 200).Write("after a restart");

        Assert.True(File.Exists(path + ".1"));
        Assert.Single(File.ReadAllLines(path));
    }

    [Fact]
    public void ALineThatCannotBeWritten_IsLost_WithoutThrowing()
    {
        using var temp = new TempDirectory();
        File.WriteAllText(temp.File("a-file"), "");
        var log = new AgentLog(Path.Combine(temp.File("a-file"), "agent.log"));

        log.Write("nowhere to go");
    }

    [Fact]
    public void ItCanBeReadWhileItIsWrittenTo()
    {
        using var temp = new TempDirectory();
        var log = new AgentLog(temp.File("agent.log"));
        log.Write("one");

        using var reader = new FileStream(
            log.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        log.Write("two");

        Assert.Equal(2, File.ReadAllLines(log.FilePath).Length);
    }
}
