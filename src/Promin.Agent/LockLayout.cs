namespace Promin.Agent;

/// <summary>The sizes of look A of the lock screen, as shares of the screen's width, so that it looks the same at any resolution.</summary>
internal static class LockLayout
{
    public const double GlyphShare = 0.07;
    public const double SmallLineShare = 0.027;
    public const double GlyphGapShare = 0.03;
    public const double SmallLineGapShare = 0.016;
    public const double SideMarginShare = 0.06;

    /// <summary>
    /// A share of the message's font height, not the screen's width: what Windows leaves above and below a line of
    /// Segoe UI (1.33 em) beyond the look's line height (1.15 em).
    /// </summary>
    public const double SpareLeadingShare = 0.09;

    private const double LargestMessageShare = 0.076;

    /// <summary>The share of the screen's height a message may take, which leaves room for the glyph and the small line.</summary>
    private const int MessagePercentOfHeight = 45;

    /// <summary>
    /// The font height for a message: the largest size, stepping down by a tenth until
    /// <paramref name="heightOfMessageAt"/> fits the room, and never below the small line's size, where a message
    /// that still does not fit is left to overflow.
    /// </summary>
    /// <param name="heightOfMessageAt">How tall the message is at a font height, wrapped to the screen.</param>
    public static int MessageFontHeight(int screenWidth, int screenHeight, Func<int, int> heightOfMessageAt)
    {
        var smallest = Share(screenWidth, SmallLineShare);
        var room = screenHeight * MessagePercentOfHeight / 100;
        var font = Share(screenWidth, LargestMessageShare);
        while (font > smallest && heightOfMessageAt(font) > room)
        {
            font = Math.Max(smallest, font * 9 / 10);
        }

        return font;
    }

    /// <summary><paramref name="share"/> of <paramref name="size"/>, in whole pixels.</summary>
    public static int Share(int size, double share) => (int)Math.Round(size * share);
}
