using System.Globalization;
using Promin.Master.Core.Sending;

namespace Promin.Master.Core.Tests.Sending;

public sealed class SendTextTests
{
    [Theory]
    [InlineData(0L, "0 KB")]
    [InlineData(1L, "1 KB")]
    [InlineData(1_023L, "1 KB")]
    [InlineData(48_000L, "47 KB")]
    [InlineData(49_152L, "48 KB")]
    [InlineData(1_047_552L, "1023 KB")]
    [InlineData(1_047_553L, "1.0 MB")]
    [InlineData(1_048_575L, "1.0 MB")]
    [InlineData(1_048_576L, "1.0 MB")]
    [InlineData(1_258_291L, "1.2 MB")]
    [InlineData(10_485_759L, "10 MB")]
    [InlineData(10_485_760L, "10 MB")]
    [InlineData(327_155_712L, "312 MB")]
    [InlineData(1_073_217_535L, "1023 MB")]
    [InlineData(1_073_217_536L, "1.0 GB")]
    [InlineData(1_073_741_823L, "1.0 GB")]
    [InlineData(1_503_238_553L, "1.4 GB")]
    public void Size_IsShownInTheUnitThatKeepsItShort(long bytes, string expected) =>
        Assert.Equal(expected, SendText.Size(bytes, CultureInfo.InvariantCulture));

    [Fact]
    public void Size_UsesTheCulturesDecimalSeparator() =>
        Assert.Equal("1,2 MB", SendText.Size(1_258_291, CultureInfo.GetCultureInfo("uk")));

    [Theory]
    [InlineData(49_152L, "48 КБ")]
    [InlineData(1_258_291L, "1,2 МБ")]
    [InlineData(1_503_238_553L, "1,4 ГБ")]
    public void Size_InUkrainian_NamesTheUnitInUkrainian(long bytes, string expected)
    {
        var before = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("uk-UA");
        try
        {
            Assert.Equal(expected, SendText.Size(bytes, CultureInfo.GetCultureInfo("uk-UA")));
        }
        finally
        {
            CultureInfo.CurrentUICulture = before;
        }
    }
}
