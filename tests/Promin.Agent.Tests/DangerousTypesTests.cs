namespace Promin.Agent.Tests;

public class DangerousTypesTests
{
    [Theory]
    [InlineData("x.exe")]
    [InlineData("x.bat")]
    [InlineData("x.cmd")]
    [InlineData("x.msi")]
    public void ProgramsAreDangerous(string name)
    {
        Assert.True(DangerousTypes.IsDangerous(name));
    }

    [Theory]
    [InlineData("x.html")]
    [InlineData("x.pdf")]
    [InlineData("x.mp4")]
    [InlineData("x.docx")]
    [InlineData("x")]
    [InlineData(@"C:\x\a.html")]
    public void DocumentsAreNot(string name)
    {
        Assert.False(DangerousTypes.IsDangerous(name));
    }

    // Win32 drops trailing dots and spaces from a path, so these open the program.
    [Theory]
    [InlineData("x.exe.")]
    [InlineData("x.exe ")]
    [InlineData("x.exe . ")]
    public void TrailingDotsAndSpaces_DoNotHideAProgram(string name)
    {
        Assert.True(DangerousTypes.IsDangerous(name));
    }

    // A colon after the drive names an alternate data stream; what it holds is not judged by its extension.
    [Theory]
    [InlineData(@"C:\x\a.exe:s")]
    [InlineData(@"C:\x\a.html:s")]
    public void AnAlternateDataStream_IsDangerous(string path)
    {
        Assert.True(DangerousTypes.IsDangerous(path));
    }

    [Fact]
    public void AFolderIsNot()
    {
        using var folder = new TempDirectory();

        Assert.False(DangerousTypes.IsDangerous(folder.Path));
    }
}
