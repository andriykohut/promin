namespace Promin.Agent.Tests;

public class ReceivedCommandTests
{
    private const string Folder = @"C:\Users\p\Desktop\Promin";

    private readonly List<string> _opened = [];
    private readonly List<(int Count, string Folder)> _notices = [];

    private int Run(int count, string[] paths, Func<string, bool>? isDangerous = null, Func<string, bool>? open = null) =>
        ReceivedCommand.Run(
            count, Folder, paths,
            isDangerous ?? (_ => false),
            open ?? (path => { _opened.Add(path); return true; }),
            (shown, folder) => _notices.Add((shown, folder)));

    [Fact]
    public void OpensEveryPathThatIsNotDangerous()
    {
        var opened = Run(2, ["a.html", "b.pdf"]);

        Assert.Equal(2, opened);
        Assert.Equal(["a.html", "b.pdf"], _opened);
        Assert.Empty(_notices);
    }

    [Fact]
    public void SkipsDangerousPaths_AndNotifiesWhenNothingOpened()
    {
        var opened = Run(1, ["tool.exe"], isDangerous: path => path.EndsWith(".exe"));

        Assert.Equal(0, opened);
        Assert.Empty(_opened);
        Assert.Equal([(1, Folder)], _notices);
    }

    [Fact]
    public void NotifiesWhenEveryOpenFails()
    {
        var opened = Run(1, ["a.html"], open: _ => false);

        Assert.Equal(0, opened);
        Assert.Equal([(1, Folder)], _notices);
    }

    [Fact]
    public void NoPaths_Notifies()
    {
        var opened = Run(3, []);

        Assert.Equal(0, opened);
        Assert.Equal([(3, Folder)], _notices);
    }
}
