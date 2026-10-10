using System.Globalization;

namespace Promin.Master.Core;

/// <summary>The language the console's texts are in for this run.</summary>
public static class AppLanguage
{
    /// <summary>
    /// Makes <paramref name="language"/> (a culture name such as <c>uk</c>) the language of every
    /// text built from now on. Null, or a name Windows does not know, leaves the Windows display
    /// language in charge. Call it before anything reads a text: some are read once and kept.
    /// </summary>
    public static void Apply(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
        {
            return;
        }

        CultureInfo culture;
        try
        {
            culture = CultureInfo.GetCultureInfo(language);
        }
        catch (CultureNotFoundException)
        {
            return;
        }

        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.CurrentUICulture = culture;

        // Dates and numbers follow the chosen language too, unless Windows already formats in it.
        if (CultureInfo.CurrentCulture.TwoLetterISOLanguageName != culture.TwoLetterISOLanguageName)
        {
            CultureInfo.DefaultThreadCurrentCulture = CultureInfo.CurrentCulture = culture;
        }
    }
}
