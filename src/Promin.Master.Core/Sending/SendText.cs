using System.Globalization;

namespace Promin.Master.Core.Sending;

/// <summary>The wording of sizes in the send window.</summary>
public static class SendText
{
    private const long KB = 1024;
    private const long MB = KB * 1024;
    private const long GB = MB * 1024;

    /// <summary>
    /// A size in 1024-based units: whole KB, rounded up, under 1 MB ("48 KB"); one decimal under 10 MB ("1.2 MB");
    /// whole MB under 1 GB ("312 MB"); one decimal from 1 GB ("1.4 GB"). The unit names are not translated.
    /// </summary>
    public static string Size(long bytes, CultureInfo culture) => bytes switch
    {
        < MB => $"{(bytes + KB - 1) / KB} KB",
        < 10 * MB => $"{((double)bytes / MB).ToString("0.0", culture)} MB",
        < GB => $"{((double)bytes / MB).ToString("0", culture)} MB",
        _ => $"{((double)bytes / GB).ToString("0.0", culture)} GB",
    };
}
