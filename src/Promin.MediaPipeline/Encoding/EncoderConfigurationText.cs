namespace Promin.MediaPipeline.Encoding;

internal static class EncoderConfigurationText
{
    /// <summary>
    /// The text <c>MediaFoundationH264Encoder.Configuration</c> shows: one line for each hardware encoder that
    /// was passed over and why, then one line of what was configured on the one in use. Without the first
    /// lines, a machine that quietly landed on software encoding gives no clue why.
    /// </summary>
    internal static string Compose(IReadOnlyList<(string Name, string Reason)> rejected, IReadOnlyList<string> notes)
    {
        var lines = new List<string>();
        foreach (var (name, reason) in rejected)
            lines.Add($"rejected hardware encoder {OneLine(name)}: {OneLine(reason)}");
        lines.Add(string.Join("; ", notes));
        return string.Join('\n', lines);
    }

    private static string OneLine(string text) =>
        string.Join(' ', text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
}
