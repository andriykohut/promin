using System.Globalization;
using Promin.Master.Core.Sending;

namespace Promin.Master.Core.Tests.Sending;

public sealed class SendTextTests
{
    [Theory]
    [InlineData(0L, "0 KB")]
    [InlineData(48_000L, "47 KB")]
    [InlineData(49_152L, "48 KB")]
    [InlineData(1_258_291L, "1.2 MB")]
    [InlineData(327_155_712L, "312 MB")]
    [InlineData(1_503_238_553L, "1.4 GB")]
    public void Size_IsShownInTheUnitThatKeepsItShort(long bytes, string expected) =>
        Assert.Equal(expected, SendText.Size(bytes, CultureInfo.InvariantCulture));

    [Fact]
    public void Size_UsesTheCulturesDecimalSeparator() =>
        Assert.Equal("1,2 MB", SendText.Size(1_258_291, CultureInfo.GetCultureInfo("uk")));
}
