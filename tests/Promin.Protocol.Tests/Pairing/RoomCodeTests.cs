using Promin.Protocol.Pairing;

namespace Promin.Protocol.Tests.Pairing;

public class RoomCodeTests
{
    [Theory]
    [InlineData("482913", true)]
    [InlineData("000000", true)]
    [InlineData("48291", false)]      // five digits
    [InlineData("4829134", false)]    // seven digits
    [InlineData("48291a", false)]     // a letter
    [InlineData("482 913", false)]    // IsValid is strict; TryNormalize is the lenient door
    [InlineData("", false)]
    public void IsValid_AcceptsExactlySixAsciiDigits(string code, bool expected) =>
        Assert.Equal(expected, RoomCode.IsValid(code));

    [Fact]
    public void IsValid_RefusesFullWidthDigits() =>
        Assert.False(RoomCode.IsValid("４８２９１３"));

    [Theory]
    [InlineData("482913")]
    [InlineData("482 913")]
    [InlineData("482-913")]
    [InlineData(" 482913 ")]
    public void TryNormalize_AcceptsGroupedDigits(string typed)
    {
        Assert.True(RoomCode.TryNormalize(typed, out var code));
        Assert.Equal("482913", code);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("482 91")]
    [InlineData("4829134")]
    [InlineData("48291x")]
    [InlineData("482.913")]
    public void TryNormalize_RefusesEverythingElse(string? typed)
    {
        Assert.False(RoomCode.TryNormalize(typed, out var code));
        Assert.Equal(string.Empty, code);
    }
}
