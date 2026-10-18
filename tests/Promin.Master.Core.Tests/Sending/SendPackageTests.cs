using System.Diagnostics;
using Promin.Master.Core.Sending;

namespace Promin.Master.Core.Tests.Sending;

public sealed class SendPackageTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "promin-tests", Guid.NewGuid().ToString("N"));

    public SendPackageTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private string Write(string relative, string content = "x")
    {
        var path = Path.Combine(_directory, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private static SendPackage From(string path, bool isFolder) => SendPackage.From([new PickedItem(path, isFolder)]);

    [Fact]
    public void AFile_IsOneItemWithItsSize()
    {
        var path = Write("notes.txt", "twelve bytes");

        var package = From(path, isFolder: false);

        var item = Assert.Single(package.Items);
        Assert.Equal("notes.txt", item.Path);
        Assert.Equal(12, item.Length);
        Assert.False(item.IsFolder);
        using var opened = item.Open!();
        Assert.Equal(12, opened.Length);
    }

    [Fact]
    public void AFolder_CarriesItsTree_WithSlashes_AndEmptyFoldersKept()
    {
        Write(@"Lesson\a.txt");
        Write(@"Lesson\sub\b.txt");
        Write(@"Lesson\sub\deep\c.txt");
        Directory.CreateDirectory(Path.Combine(_directory, "Lesson", "empty"));

        var package = From(Path.Combine(_directory, "Lesson"), isFolder: true);

        var paths = package.Items.Select(i => i.Path).ToList();
        Assert.Equal(
            ["Lesson", "Lesson/a.txt", "Lesson/empty", "Lesson/sub", "Lesson/sub/b.txt", "Lesson/sub/deep", "Lesson/sub/deep/c.txt"],
            paths.Order(StringComparer.Ordinal));
        Assert.All(package.Items.Where(i => i.IsFolder), i => Assert.Null(i.Open));
        Assert.All(package.Items.Where(i => i.IsFolder), i => Assert.Equal(0, i.Length));
        Assert.All(package.Items.Where(i => !i.IsFolder), i => Assert.NotNull(i.Open));
        foreach (var path in paths.Where(p => p.Contains('/')))
        {
            Assert.True(paths.IndexOf(path[..path.LastIndexOf('/')]) < paths.IndexOf(path), $"{path} comes before its folder.");
        }
    }

    [Fact]
    public void HiddenAndSystemFilesAndLinks_AreLeftOut()
    {
        Write(@"Lesson\kept.txt");
        File.SetAttributes(Write(@"Lesson\desktop.ini"), FileAttributes.Hidden | FileAttributes.System);
        File.SetAttributes(Write(@"Lesson\secret.txt"), FileAttributes.Hidden);
        Write(@"Lesson\.cache\inside.txt");
        File.SetAttributes(Path.Combine(_directory, "Lesson", ".cache"), FileAttributes.Hidden);
        Write(@"Elsewhere\outside.txt");
        var junction = Path.Combine(_directory, "Lesson", "link");
        MakeJunction(junction, Path.Combine(_directory, "Elsewhere"));

        try
        {
            var package = From(Path.Combine(_directory, "Lesson"), isFolder: true);

            Assert.Equal(["Lesson", "Lesson/kept.txt"], package.Items.Select(i => i.Path).Order(StringComparer.Ordinal));
        }
        finally
        {
            Directory.Delete(junction);
        }
    }

    [Fact]
    public void TotalBytes_IsTheSumOfFiles_AndSizeOfAgreesForAFolder()
    {
        Write(@"Lesson\a.txt", "12345");
        Write(@"Lesson\sub\b.txt", "123");
        var lone = Write("lone.bin", "1234567");
        Directory.CreateDirectory(Path.Combine(_directory, "Lesson", "empty"));
        var lesson = new PickedItem(Path.Combine(_directory, "Lesson"), IsFolder: true);

        var package = SendPackage.From([lesson, new PickedItem(lone, IsFolder: false)]);

        Assert.Equal(15, package.TotalBytes);
        Assert.Equal(8, SendPackage.SizeOf(lesson));
        Assert.Equal(7, SendPackage.SizeOf(new PickedItem(lone, IsFolder: false)));
        Assert.Equal(2, package.Picked.Count);
    }

    [Fact]
    public void UnicodeNames_Survive()
    {
        Write(@"Тест 1\звіт.docx", "дані");

        var package = From(Path.Combine(_directory, "Тест 1"), isFolder: true);

        Assert.Equal(["Тест 1", "Тест 1/звіт.docx"], package.Items.Select(i => i.Path));
        Assert.Equal(8, package.TotalBytes);
    }

    [Fact]
    public void AFolderThatIsGone_ThrowsAnIOException()
    {
        Assert.ThrowsAny<IOException>(() => From(Path.Combine(_directory, "nowhere"), isFolder: true));
    }

    // A junction needs no elevation, unlike a symbolic link.
    private static void MakeJunction(string link, string target)
    {
        var start = new ProcessStartInfo("cmd.exe") { CreateNoWindow = true, RedirectStandardOutput = true };
        start.ArgumentList.Add("/c");
        start.ArgumentList.Add("mklink");
        start.ArgumentList.Add("/J");
        start.ArgumentList.Add(link);
        start.ArgumentList.Add(target);
        using var process = Process.Start(start)!;
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }
}
