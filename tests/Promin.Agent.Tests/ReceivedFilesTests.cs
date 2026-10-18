using System.Text;
using Promin.Protocol.Messages;
using Promin.Protocol.Sending;

namespace Promin.Agent.Tests;

public sealed class ReceivedFilesTests : IDisposable
{
    private readonly TempDirectory home = new();
    private readonly string staging;
    private readonly string promin;

    public ReceivedFilesTests()
    {
        staging = home.File(@"LocalAppData\Promin\Incoming");
        var desktop = home.File("Desktop");
        Directory.CreateDirectory(desktop);
        promin = Path.Combine(desktop, "Promin");
    }

    public void Dispose() => home.Dispose();

    [Fact]
    public void AnItem_AppearsOnlyAfterFinish()
    {
        var target = NewTarget();
        target.Begin(3);
        Write(target, "quiz.html", "new");

        Assert.False(File.Exists(InPromin("quiz.html")));
        Assert.Equal(SendOutcome.Sent, target.Finish());
        Assert.Equal("new", File.ReadAllText(InPromin("quiz.html")));
        Assert.False(Directory.Exists(staging));
        Assert.Null(target.Failure);
    }

    [Fact]
    public void Discard_LeavesNothing()
    {
        var target = NewTarget();
        target.Begin(3);
        Write(target, "quiz.html", "new");

        target.Discard();

        Assert.False(File.Exists(InPromin("quiz.html")));
        Assert.False(Directory.Exists(staging));
    }

    [Fact]
    public void AFileReplacesTheOldCopy()
    {
        Place("a.txt", "old");

        Assert.Equal(SendOutcome.Sent, Send(("a.txt", "new")));
        Assert.Equal("new", File.ReadAllText(InPromin("a.txt")));
    }

    [Fact]
    public void AFolderMerges_KeepingFilesItDidNotCarry()
    {
        Place(@"w\mine.txt", "mine");

        Assert.Equal(SendOutcome.Sent, Send(("w/new.txt", "new")));
        Assert.Equal("mine", File.ReadAllText(InPromin(@"w\mine.txt")));
        Assert.Equal("new", File.ReadAllText(InPromin(@"w\new.txt")));
    }

    [Fact]
    public void EmptyFoldersAreKept()
    {
        var target = NewTarget();
        target.Begin(0);
        target.AddFolder("empty");
        target.AddFolder("w");
        target.AddFolder("w/inner");

        Assert.Equal(SendOutcome.Sent, target.Finish());
        Assert.True(Directory.Exists(InPromin("empty")));
        Assert.True(Directory.Exists(InPromin(@"w\inner")));
    }

    [Fact]
    public void AnOldCopyThatIsOpen_IsInUse_AndTheOtherItemsStillLand()
    {
        Place("a.txt", "old");

        var target = NewTarget();
        using (new FileStream(InPromin("a.txt"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Equal(SendOutcome.InUse, Send(target, ("a.txt", "new"), ("b.txt", "new")));
        }

        Assert.Equal("old", File.ReadAllText(InPromin("a.txt")));
        Assert.Equal("new", File.ReadAllText(InPromin("b.txt")));
        Assert.Contains(InPromin("a.txt"), target.Failure!.Message);
    }

    // Items move in name order, so "0" clashes before a.txt is found open and "w" after.
    [Theory]
    [InlineData("0")]
    [InlineData("w")]
    public void AClashAndAnOpenOldCopy_FailWithTheClashAsTheReason(string clash)
    {
        Place("a.txt", "old");
        Place(Path.Combine(clash, "mine.txt"), "mine");

        var target = NewTarget();
        using (new FileStream(InPromin("a.txt"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Equal(SendOutcome.Failed, Send(target, ("a.txt", "new"), (clash, "new")));
        }

        Assert.Contains(InPromin(clash), target.Failure!.Message);
    }

    // Replacing it is denied as for an open file, but nothing holds it, so it is not "in use".
    [Fact]
    public void AReadOnlyOldCopy_Fails()
    {
        Place("a.txt", "old");
        File.SetAttributes(InPromin("a.txt"), FileAttributes.ReadOnly);

        try
        {
            Assert.Equal(SendOutcome.Failed, Send(("a.txt", "new")));
            Assert.Equal("old", File.ReadAllText(InPromin("a.txt")));
        }
        finally
        {
            File.SetAttributes(InPromin("a.txt"), FileAttributes.Normal);
        }
    }

    [Fact]
    public void NotEnoughSpace_IsRefusedAtBegin()
    {
        var refused = Assert.Throws<SendRefusedException>(() => NewTarget(freeBytes: 10).Begin(11));

        Assert.Equal(SendOutcome.NotEnoughSpace, refused.Outcome);
        NewTarget(freeBytes: 10).Begin(10);
    }

    [Fact]
    public void TooLittleRoomOnTheDesktopDrive_IsRefusedAtBegin()
    {
        var target = new ReceivedFiles(staging, promin, path => path == staging ? long.MaxValue : 10);

        Assert.Equal(SendOutcome.NotEnoughSpace, Assert.Throws<SendRefusedException>(() => target.Begin(11)).Outcome);
    }

    [Fact]
    public void ALeftoverFromAnEarlierCrash_IsRemovedAtBegin()
    {
        Directory.CreateDirectory(staging);
        File.WriteAllText(Path.Combine(staging, "left.txt"), "left");

        NewTarget().Begin(0);

        Assert.False(File.Exists(Path.Combine(staging, "left.txt")));
    }

    [Fact]
    public void AProminFolderThatDoesNotExistYet_IsCreated()
    {
        Assert.False(Directory.Exists(promin));

        Assert.Equal(SendOutcome.Sent, Send(("a.txt", "new")));
        Assert.Equal("new", File.ReadAllText(InPromin("a.txt")));
    }

    [Fact]
    public void AFileNamedPromin_OnTheDesktop_Fails()
    {
        File.WriteAllText(promin, "mine");

        var target = NewTarget();
        Assert.Equal(SendOutcome.Failed, Send(target, ("a.txt", "new")));
        Assert.Equal("mine", File.ReadAllText(promin));
        Assert.Contains(promin, target.Failure!.Message);
        Assert.False(Directory.Exists(staging));
    }

    [Fact]
    public void AFolderWhereAFileIs_Fails()
    {
        Place("w", "mine");

        var target = NewTarget();
        Assert.Equal(SendOutcome.Failed, Send(target, ("w/a.txt", "new")));
        Assert.Equal("mine", File.ReadAllText(InPromin("w")));
        Assert.Contains(InPromin("w"), target.Failure!.Message);
    }

    [Fact]
    public void AFileWhereAFolderIs_Fails()
    {
        Place(@"w\mine.txt", "mine");

        var target = NewTarget();
        Assert.Equal(SendOutcome.Failed, Send(target, ("w", "new")));
        Assert.Equal("mine", File.ReadAllText(InPromin(@"w\mine.txt")));
        Assert.Contains(InPromin("w"), target.Failure!.Message);
    }

    [Fact]
    public void ABadPath_IsRefused()
    {
        var target = NewTarget();
        target.Begin(1);

        Assert.Equal(SendOutcome.BadPath, Assert.Throws<SendRefusedException>(() => target.AddFile("../x", 1)).Outcome);
        Assert.Equal(SendOutcome.BadPath, Assert.Throws<SendRefusedException>(() => target.AddFolder("..")).Outcome);
    }

    [Fact]
    public void TopLevel_ListsEachItemOnce()
    {
        var target = NewTarget();
        target.Begin(2);
        Write(target, "quiz.html", "q");
        target.AddFolder("quiz_files");
        Write(target, "quiz_files/a.png", "a");

        Assert.Equal([("quiz.html", false), ("quiz_files", true)], target.TopLevel);
    }

    private ReceivedFiles NewTarget(long freeBytes = long.MaxValue) => new(staging, promin, _ => freeBytes);

    private string InPromin(string relative) => Path.Combine(promin, relative);

    /// <summary>Puts a file in Promin before the send, as the person's own.</summary>
    private void Place(string relative, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(InPromin(relative))!);
        File.WriteAllText(InPromin(relative), text);
    }

    private static void Write(ReceivedFiles target, string path, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        using var stream = target.AddFile(path, bytes.Length);
        stream.Write(bytes);
    }

    private SendOutcome Send(params (string Path, string Text)[] files) => Send(NewTarget(), files);

    /// <summary>A whole send of the given files, returning what <see cref="ReceivedFiles.Finish"/> says.</summary>
    private static SendOutcome Send(ReceivedFiles target, params (string Path, string Text)[] files)
    {
        target.Begin(files.Sum(file => Encoding.UTF8.GetByteCount(file.Text)));
        foreach (var (path, text) in files)
        {
            Write(target, path, text);
        }

        return target.Finish();
    }
}
