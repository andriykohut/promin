namespace Promin.Agent.Tests;

public class OpenPlanTests
{
    private const string Promin = @"C:\Users\someone\Desktop\Promin";

    [Fact]
    public void Unticked_OpensNothing()
    {
        Assert.Empty(OpenPlan.For([("quiz.html", false), ("photos", true)], open: false, Promin));
    }

    [Fact]
    public void FilesAddedDirectly_Open_FolderContentsDoNot()
    {
        Assert.Equal(
            [Path.Combine(Promin, "quiz.html")],
            OpenPlan.For([("quiz.html", false), ("quiz_files", true)], open: true, Promin));
    }

    [Fact]
    public void AFolderOnItsOwn_Opens()
    {
        Assert.Equal([Path.Combine(Promin, "photos")], OpenPlan.For([("photos", true)], open: true, Promin));
    }

    [Fact]
    public void TwoFoldersOnly_OpenNothing()
    {
        Assert.Empty(OpenPlan.For([("photos", true), ("videos", true)], open: true, Promin));
    }

    // The helper skips what Windows calls dangerous; the plan only says what was sent directly.
    [Fact]
    public void ProgramsArePassedOn()
    {
        Assert.Contains(
            Path.Combine(Promin, "tool.exe"),
            OpenPlan.For([("tool.exe", false), ("notes.txt", false)], open: true, Promin));
    }
}
