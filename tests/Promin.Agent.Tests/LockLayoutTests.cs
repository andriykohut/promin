namespace Promin.Agent.Tests;

public class LockLayoutTests
{
    [Fact]
    public void AShortMessage_IsLarge() =>
        Assert.Equal(146, LockLayout.MessageFontHeight(1920, 1080, _ => 150)); // 7.6 % of the width

    [Theory]
    [InlineData(1920, 1080)]
    [InlineData(1024, 768)]
    public void ALongMessage_ShrinksUntilItFits(int width, int height)
    {
        var font = LockLayout.MessageFontHeight(width, height, font => font * 6); // six lines at any size

        Assert.True(font * 6 <= height * 45 / 100);
    }

    [Fact]
    public void AMessageThatNeverFits_StopsAtTheSmallestSize() =>
        Assert.Equal(52, LockLayout.MessageFontHeight(1920, 1080, _ => int.MaxValue)); // 2.7 % of the width
}
