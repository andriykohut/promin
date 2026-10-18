namespace Promin.Agent.Tests;

public class ReceivedPathTests
{
    private const string Root = @"C:\Users\someone\Desktop\Promin";

    [Theory]
    [InlineData("")]
    [InlineData("/a")]
    [InlineData("\\a")]
    [InlineData("C:a")]
    [InlineData("C:/a")]
    [InlineData("a/../b")]
    [InlineData("..")]
    [InlineData("a//b")]
    [InlineData("a/./b")]
    [InlineData("CON")]
    [InlineData("con.txt")]
    [InlineData("a/LPT1")]
    [InlineData("COM¹")]
    [InlineData("a:b")]
    [InlineData("a/b ")]
    [InlineData("a/b.")]
    [InlineData("a*b")]
    [InlineData("a\\b")]
    public void RefusesWhatCouldLeaveTheFolderOrIsNotAPlainName(string relative)
    {
        Assert.False(ReceivedPath.TryJoin(Root, relative, out _));
    }

    [Theory]
    [InlineData("quiz.html")]
    [InlineData("quiz_files/img 1.png")]
    [InlineData("Тест 1/звіт.docx")]
    [InlineData("a/b/c.txt")]
    public void AcceptsPlainRelativePaths(string relative)
    {
        Assert.True(ReceivedPath.TryJoin(Root, relative, out var full));
        Assert.Equal(Path.Combine(Root, relative.Replace('/', '\\')), full);
    }
}
