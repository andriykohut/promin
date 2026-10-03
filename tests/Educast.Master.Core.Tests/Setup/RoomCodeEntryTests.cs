using Educast.Master.Core.Setup;

namespace Educast.Master.Core.Tests.Setup;

/// <summary>What the room code field contains after each kind of input (fix round 1, B1).</summary>
public sealed class RoomCodeEntryTests
{
    [Theory]
    [InlineData("", "4", "4")]                   // a digit typed
    [InlineData("4829", "48291", "48291")]       // another digit typed
    [InlineData("48291", "482913", "482913")]    // the sixth digit
    [InlineData("482913", "4829134", "482913")]  // a seventh digit is refused
    [InlineData("4829", "4829a", "4829")]        // a letter is refused
    [InlineData("4829", "4829 ", "4829")]        // a space typed is refused
    [InlineData("482913", "48291", "48291")]     // a deletion
    [InlineData("482913", "", "")]               // cleared
    [InlineData("", "482 913", "482913")]        // a paste with a space is normalised
    [InlineData("", "482-913", "482913")]        // a paste with a hyphen is normalised
    [InlineData("", " 482913 ", "482913")]       // a paste with ends trimmed
    [InlineData("48", "482 913", "482913")]      // a paste into a field that already has digits
    [InlineData("48", "48hello", "48")]          // a paste of junk leaves the field unchanged
    [InlineData("", "４８２９１３", "")]            // full-width digits are refused
    [InlineData("48", "482.913", "48")]          // a separator the code does not allow
    public void Accept_DecidesWhatTheFieldContains(string current, string proposed, string expected) =>
        Assert.Equal(expected, RoomCodeEntry.Accept(current, proposed));
}
