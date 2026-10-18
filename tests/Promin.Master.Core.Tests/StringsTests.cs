using System.Collections;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Promin.Master.Core.Wall;

namespace Promin.Master.Core.Tests;

public sealed class StringsTests
{
    private static readonly CultureInfo Ukrainian = CultureInfo.GetCultureInfo("uk");

    // The wording tests in this project state the English texts, whatever language Windows is in.
    [ModuleInitializer]
    internal static void WordingTestsReadEnglish() =>
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en");

    [Fact]
    public void EveryText_HasAUkrainianOneWithTheSameNames()
    {
        var english = Texts(CultureInfo.InvariantCulture);
        var ukrainian = Texts(Ukrainian);

        Assert.NotEmpty(english);
        foreach (var (key, text) in english)
        {
            Assert.True(ukrainian.TryGetValue(key, out var translated), $"{key} has no Ukrainian text.");
            Assert.Equal(Names(text), Names(translated!));
        }

        Assert.Empty(ukrainian.Keys.Except(english.Keys));
    }

    [Theory]
    [InlineData(1, "Пропущено 1 кадр")]
    [InlineData(2, "Пропущено 2 кадри")]
    [InlineData(5, "Пропущено 5 кадрів")]
    [InlineData(21, "Пропущено 21 кадр")]
    [InlineData(1234, "Пропущено 1234 кадри")]
    public void UkrainianCounts_TakeTheFormTheNumberNeeds(int frames, string expected) =>
        Assert.Equal(expected, In(Ukrainian, () => WallText.Status(PcTileState.DroppingFrames, frames)));

    [Theory]
    [InlineData(1, "готовий")]
    [InlineData(3, "готові")]
    [InlineData(11, "готових")]
    public void UkrainianPillWords_AgreeWithTheCount(int ready, string expected) =>
        Assert.Equal(expected, In(Ukrainian, () => WallText.PillsOffAir(ready, 0).Single().Word));

    [Theory]
    [InlineData(1, "1 елемент · 3 MB")]
    [InlineData(3, "3 елементи · 3 MB")]
    [InlineData(5, "5 елементів · 3 MB")]
    [InlineData(21, "21 елемент · 3 MB")]
    public void UkrainianSendSummary_TakesTheFormTheNumberNeeds(int items, string expected) =>
        Assert.Equal(expected, In(Ukrainian, () => Phrase.Format(Strings.Send_Summary, ("count", items), ("size", "3 MB"))));

    [Theory]
    [InlineData(1, "Вибраний ПК")]
    [InlineData(21, "Вибрані 21 ПК")]
    public void UkrainianSendChoices_NameOnlyASinglePc_ForExactlyOne(int pcs, string expected) =>
        Assert.Equal(expected, In(Ukrainian, () => Phrase.Format(Strings.Send_Selected, ("count", pcs))));

    [Fact]
    public void ANameWithBracesOrAnApostrophe_IsShownAsTyped() =>
        Assert.Equal("{PC}'s is back.", WallMessage.IsBack("{PC}'s").Title);

    private static string In(CultureInfo culture, Func<string> text)
    {
        var before = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = culture;
        try
        {
            return text();
        }
        finally
        {
            CultureInfo.CurrentUICulture = before;
        }
    }

    private static Dictionary<string, string> Texts(CultureInfo culture) =>
        Strings.ResourceManager.GetResourceSet(culture, createIfNotExists: true, tryParents: false)!
            .Cast<DictionaryEntry>()
            .ToDictionary(e => (string)e.Key, e => (string)e.Value!);

    // The names a text is filled with, such as {count}. A plural form's own brace ("other {ready}") is not one.
    private static SortedSet<string> Names(string text) =>
        new(Regex.Matches(Regex.Replace(text, @"\b(zero|one|two|few|many|other)\s*\{|=\d+\s*\{", "<"), @"\{(\w+)[,}]")
            .Select(m => m.Groups[1].Value));
}
