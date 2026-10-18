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
    public void DocumentsAreNot(string name)
    {
        Assert.False(DangerousTypes.IsDangerous(name));
    }

    [Fact]
    public void AFolderIsNot()
    {
        using var folder = new TempDirectory();

        Assert.False(DangerousTypes.IsDangerous(folder.Path));
    }
}
