using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Educast.Master.Core.Tests;

public sealed class ConsoleLogTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "educast-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private string Current => Path.Combine(_directory, "console.log");

    private string Previous => Path.Combine(_directory, "console.previous.log");

    [Fact]
    public void TryOpen_MakesTheFolder_AndWritesEachLineWithItsTime_ReadableWhileOpen()
    {
        using var log = ConsoleLog.TryOpen(_directory);

        Assert.NotNull(log);
        log.TraceEvent(null, "Educast", TraceEventType.Information, 0, "Broadcast started: {0} of {1} PCs receiving.", 9, 10);
        log.TraceEvent(null, "Educast", TraceEventType.Error, 0, "The broadcast could not start.");
        log.Flush();

        using var reader = new StreamReader(new FileStream(Current, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
        var lines = reader.ReadToEnd().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.Matches(@"^\d{4}-\d\d-\d\d \d\d:\d\d:\d\d\.\d{3}  Information: Broadcast started: 9 of 10 PCs receiving\.$", lines[0]);
        Assert.Matches(@"^\d{4}-\d\d-\d\d \d\d:\d\d:\d\d\.\d{3}  Error: The broadcast could not start\.$", lines[1]);
    }

    [Fact]
    public void TryOpen_KeepsTheLastRunAsPrevious_ReplacingTheOneBeforeIt()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Current, "the last run");
        File.WriteAllText(Previous, "the run before it");

        using (var log = ConsoleLog.TryOpen(_directory))
        {
            Assert.NotNull(log);
        }

        Assert.Equal("the last run", File.ReadAllText(Previous));
        Assert.Equal(string.Empty, File.ReadAllText(Current));
    }

    [Fact]
    public void TryOpen_IsNull_WhenTheLogCannotBeOpened()
    {
        Directory.CreateDirectory(_directory);
        var notAFolder = Path.Combine(_directory, "file");
        File.WriteAllText(notAFolder, string.Empty);

        Assert.Null(ConsoleLog.TryOpen(notAFolder));
    }

    [Fact]
    public void TryOpen_IsNull_WhileAnotherConsoleHoldsTheLog_AndLeavesItsLogAlone()
    {
        using var first = ConsoleLog.TryOpen(_directory);
        Assert.NotNull(first);
        first.TraceEvent(null, "Educast", TraceEventType.Information, 0, "first");
        first.Flush();

        Assert.Null(ConsoleLog.TryOpen(_directory));
        Assert.False(File.Exists(Previous));
    }
}
