using Promin.Protocol.Messages;

namespace Promin.Protocol.Tests.Messages;

public class LockLimitsTests
{
    [Theory]
    [InlineData("  Break until 11:30 ", "Break until 11:30")]
    [InlineData("line one\r\nline two", "line one  line two")]
    [InlineData("   ", "")]
    public void Clean_TrimsAndKeepsItToOneLine(string typed, string expected) =>
        Assert.Equal(expected, LockLimits.Clean(typed));

    [Fact]
    public void Clean_CutsToTheLimit() =>
        Assert.Equal(LockLimits.MaxMessageLength, LockLimits.Clean(new string('x', 300)).Length);

    [Fact]
    public void Clean_NeverLeavesHalfACharacter()
    {
        var cleaned = LockLimits.Clean(new string('x', 99) + "😀");
        Assert.False(char.IsHighSurrogate(cleaned[^1]));
    }
}
