using System.Globalization;

namespace Promin.Master.Core.Sending;

/// <summary>The wording of sizes in the send window, and of why a PC did not get a send.</summary>
public static class SendText
{
    private const long KB = 1024;
    private const long MB = KB * 1024;
    private const long GB = MB * 1024;

    /// <summary>
    /// A size in 1024-based units: whole KB, rounded up ("48 KB"); one decimal under 10 MB ("1.2 MB"); whole MB under
    /// 1 GB ("312 MB"); one decimal from 1 GB ("1.4 GB"). A size moves up to the next unit when rounding would make it
    /// reach that unit's lower bound, so it never reads "1024 KB", "10.0 MB" or "1024 MB". The number follows
    /// <paramref name="culture"/>; the unit's name follows the current UI culture.
    /// </summary>
    public static string Size(long bytes, CultureInfo culture)
    {
        var kb = (bytes + KB - 1) / KB;
        var mb = (double)bytes / MB;
        return kb < 1024 ? Unit(Strings.Size_KB, kb.ToString(culture))
            : Math.Round(mb, 1, MidpointRounding.AwayFromZero) < 10 ? Unit(Strings.Size_MB, mb.ToString("0.0", culture))
            : Math.Round(mb, MidpointRounding.AwayFromZero) < 1024 ? Unit(Strings.Size_MB, mb.ToString("0", culture))
            : Unit(Strings.Size_GB, ((double)bytes / GB).ToString("0.0", culture));
    }

    /// <summary>The sentence a PC's details give for why it did not get the send.</summary>
    public static string NotSent(NotSentReason reason) => reason switch
    {
        NotSentReason.Offline => Strings.NotSent_Offline,
        NotSentReason.ConnectionLost => Strings.NotSent_ConnectionLost,
        NotSentReason.NotEnoughSpace => Strings.NotSent_NotEnoughSpace,
        NotSentReason.InUse => Strings.NotSent_InUse,
        NotSentReason.Stopped => Strings.NotSent_Stopped,
        _ => Strings.NotSent_Other,
    };

    /// <summary>
    /// Why one PC did not get the send, as the end of the strip's sentence about it; null for
    /// <see cref="NotSentReason.Other"/>, which the strip gives no reason for.
    /// </summary>
    public static string? Why(NotSentReason reason) => reason switch
    {
        NotSentReason.Offline => Strings.SendStrip_WhyOffline,
        NotSentReason.ConnectionLost => Strings.SendStrip_WhyConnectionLost,
        NotSentReason.NotEnoughSpace => Strings.SendStrip_WhyNotEnoughSpace,
        NotSentReason.InUse => Strings.SendStrip_WhyInUse,
        NotSentReason.Stopped => Strings.SendStrip_WhyStopped,
        _ => null,
    };

    private static string Unit(string text, string number) => Phrase.Format(text, ("size", number));
}
