using System.Globalization;
using Jeffijoe.MessageFormat;

namespace Promin.Master.Core;

/// <summary>
/// Fills in a text from <see cref="Strings"/> that has names in braces or a plural choice. The
/// texts are written in the ICU message format, so that each language can list the plural forms
/// it needs.
/// </summary>
public static class Phrase
{
    public static string Format(string text, params (string Name, object Value)[] values) =>
        MessageFormatter.Format(text, values.ToDictionary(v => v.Name, v => (object?)v.Value), CultureInfo.CurrentUICulture);
}
