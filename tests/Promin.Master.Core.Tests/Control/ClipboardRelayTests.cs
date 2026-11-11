using Promin.Master.Core.Control;

namespace Promin.Master.Core.Tests.Control;

public class ClipboardRelayTests
{
    [Fact]
    public void ACopyInTheView_IsSent() => Assert.Equal("a", new ClipboardRelay().OnLocalCopy("a", true));

    [Fact]
    public void ACopyElsewhere_IsNotSent_UntilTheViewIsFocused()
    {
        var relay = new ClipboardRelay();

        Assert.Null(relay.OnLocalCopy("a", false));
        Assert.Equal("a", relay.OnViewFocused("a"));
    }

    [Fact]
    public void FocusingTheViewAgain_WithNothingNew_SendsNothing()
    {
        var relay = new ClipboardRelay();
        relay.OnLocalCopy("a", true);

        Assert.Null(relay.OnViewFocused("a"));
    }

    [Fact]
    public void TextFromTheOtherPc_IsNotSentBack()
    {
        var relay = new ClipboardRelay();
        relay.OnRemoteText("x");

        Assert.Null(relay.OnLocalCopy("x", true));
        Assert.Null(relay.OnViewFocused("x"));
    }

    [Fact]
    public void TextSentBefore_IsSentAgain_OnceTheOtherPcHasCopiedSomethingElse()
    {
        var relay = new ClipboardRelay();
        Assert.Equal("a", relay.OnLocalCopy("a", true));
        relay.OnRemoteText("b");

        Assert.Equal("a", relay.OnLocalCopy("a", true));
    }

    [Fact]
    public void TextFromTheOtherPc_IsSentBack_OnceSomethingElseWasSentAfterIt()
    {
        var relay = new ClipboardRelay();
        relay.OnRemoteText("x");
        Assert.Equal("y", relay.OnLocalCopy("y", true));

        Assert.Equal("x", relay.OnLocalCopy("x", true));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void NothingToSend_SendsNothing(string? text)
    {
        var relay = new ClipboardRelay();

        Assert.Null(relay.OnLocalCopy(text, true));
        Assert.Null(relay.OnViewFocused(text));
    }

    [Fact]
    public void TextOverTheCap_IsNotSent()
    {
        var relay = new ClipboardRelay();

        Assert.Null(relay.OnLocalCopy(new string('a', 10_001), true));
        Assert.NotNull(relay.OnLocalCopy(new string('a', 10_000), true));
    }
}
