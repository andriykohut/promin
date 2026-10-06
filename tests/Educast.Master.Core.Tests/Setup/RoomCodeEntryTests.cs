using Educast.Master.Core.Setup;

namespace Educast.Master.Core.Tests.Setup;

/// <summary>
/// What happens to the room code field for each input (fix round 3, B1). The field's control
/// reports an edit before it happens — where the caret or selection is and what would go in —
/// and these rules say what the field holds afterwards and where the caret goes, or refuse.
/// </summary>
public sealed class RoomCodeEntryTests
{
    // Typing, deleting, selecting and overtyping.

    [Theory]
    [InlineData("", 0, 0, "4", "4", 1)]              // the first digit
    [InlineData("48", 2, 0, "2", "482", 3)]          // a digit at the end
    [InlineData("48291", 5, 0, "3", "482913", 6)]    // the sixth digit
    [InlineData("48", 1, 0, "9", "498", 2)]          // a digit in the middle (acceptance step 5)
    [InlineData("482913", 0, 3, "1", "1913", 1)]     // overtyping a selection
    [InlineData("482913", 5, 1, "7", "482917", 6)]   // overtyping the last digit of a full field
    [InlineData("482913", 0, 6, "5", "5", 1)]        // overtyping everything
    [InlineData("482", 2, 1, "", "48", 2)]           // Backspace at the end
    [InlineData("482", 0, 1, "", "82", 0)]           // Delete at the start
    [InlineData("482913", 1, 4, "", "43", 1)]        // deleting a selection
    [InlineData("", 0, 0, "48", "48", 2)]            // several digits at once (an input method)
    public void Edit_TakesDigits_AndDeletions(string current, int start, int length, string inserted, string text, int caret)
    {
        var edit = RoomCodeEntry.Edit(current, start, length, inserted);

        Assert.Equal(new CodeEdit(text, caret), edit);
    }

    [Theory]
    [InlineData("48", 2, 0, "a")]          // a letter
    [InlineData("48", 2, 0, "$")]          // a symbol
    [InlineData("48", 2, 0, " ")]          // a space
    [InlineData("48", 2, 0, "\n")]         // Enter
    [InlineData("482913", 6, 0, "4")]      // a seventh digit
    [InlineData("482913", 3, 0, "4")]      // a seventh digit in the middle
    [InlineData("482913", 0, 1, "12")]     // two for one, which would make seven
    [InlineData("48", 0, 2, "x")]          // overtyping a selection with a letter
    [InlineData("48", 1, 0, "4a")]         // a mixed insertion (an input method)
    [InlineData("48", 2, 0, "４")]          // a full-width digit
    [InlineData("48", 1, 0, "●")]          // the mask character itself
    public void Edit_RefusesAnythingElse_AndChangesNothing(string current, int start, int length, string inserted) =>
        Assert.Null(RoomCodeEntry.Edit(current, start, length, inserted));

    [Theory]
    [InlineData("48", 5, 0, "2", "482", 3)]     // a caret past the end is treated as the end
    [InlineData("48", 1, 9, "2", "42", 2)]      // a selection past the end stops at the end
    [InlineData("48", -1, 0, "2", "248", 1)]    // a caret before the start is the start
    public void Edit_ClampsAnImpossibleSelection(string current, int start, int length, string inserted, string text, int caret) =>
        Assert.Equal(new CodeEdit(text, caret), RoomCodeEntry.Edit(current, start, length, inserted));

    // Pasting.

    [Theory]
    [InlineData("", 0, 0, "482 913", "482913", 6)]        // acceptance step 1
    [InlineData("11", 2, 0, "482-913", "482913", 6)]      // acceptance step 2: replaces whatever was there
    [InlineData("11", 0, 1, "482913", "482913", 6)]       // six digits replace, even over a selection
    [InlineData("48", 2, 0, " 482913 ", "482913", 6)]     // surrounding spaces
    [InlineData("48", 2, 0, "77", "4877", 4)]             // acceptance step 7: digits that fit go in at the caret
    [InlineData("", 0, 0, "482", "482", 3)]               // a short digit paste
    [InlineData("48", 1, 0, "7", "478", 2)]               // at the caret, not the end
    [InlineData("482913", 0, 6, "11", "11", 2)]           // digits replacing a selection
    [InlineData("4829", 2, 2, "13", "4813", 4)]           // digits replacing part of the field
    public void Paste_ReplacesWithANormalisedCode_OrInsertsDigitsThatFit(
        string current, int start, int length, string clipboard, string text, int caret) =>
        Assert.Equal(new CodeEdit(text, caret), RoomCodeEntry.Paste(current, start, length, clipboard));

    [Theory]
    [InlineData("48", 2, 0, "abc")]        // acceptance step 6
    [InlineData("48", 2, 0, "48291")]      // digits that do not fit (seven in all)
    [InlineData("482913", 6, 0, "1")]      // a full field
    [InlineData("48", 2, 0, "")]           // nothing
    [InlineData("48", 2, 0, "   ")]        // only spaces
    [InlineData("48", 2, 0, "482 91")]     // five digits with a space: neither a code nor plain digits
    [InlineData("48", 2, 0, "4829134")]    // seven digits
    [InlineData("", 0, 0, "４８２９１３")]    // full-width digits
    [InlineData("48", 2, 0, "48\n29")]     // a line break
    public void Paste_RefusesAnythingElse_AndChangesNothing(string current, int start, int length, string clipboard) =>
        Assert.Null(RoomCodeEntry.Paste(current, start, length, clipboard));

    [Fact]
    public void Mask_IsOneDotPerDigit()
    {
        Assert.Equal(string.Empty, RoomCodeEntry.Mask(string.Empty));
        Assert.Equal("●●●", RoomCodeEntry.Mask("482"));
    }
}
