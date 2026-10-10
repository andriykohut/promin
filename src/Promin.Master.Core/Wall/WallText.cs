using System.Globalization;
using Promin.Protocol.Video;

namespace Promin.Master.Core.Wall;

/// <summary>Every string the room wall shows, from counts and names, so the wording is tested on its own.</summary>
public static class WallText
{
    public static string EmptyRoomLine1 => Strings.Wall_EmptyLine1;

    public static string EmptyRoomLine2 => Strings.Wall_EmptyLine2;

    public static string OnAirLine1 => Strings.Wall_OnAir;

    public static string Status(PcTileState state, int framesDropped) => state switch
    {
        PcTileState.Ready => Strings.Status_Ready,
        PcTileState.Offline => Strings.Status_Offline,
        PcTileState.Receiving => Strings.Status_Receiving,
        PcTileState.DroppingFrames => Phrase.Format(Strings.Status_FramesDropped, ("count", framesDropped)),
        PcTileState.Reconnecting => Strings.Status_Reconnecting,
        _ => string.Empty,
    };

    /// <summary>The header pills off air. A pill whose count is 0 is left out.</summary>
    public static IReadOnlyList<WallPill> PillsOffAir(int ready, int offline) =>
        Pills((PcTileState.Ready, ready, Strings.Pill_Ready), (PcTileState.Offline, offline, Strings.Pill_Offline));

    /// <summary>The header pills on air. A pill whose count is 0 is left out.</summary>
    public static IReadOnlyList<WallPill> PillsOnAir(int receiving, int dropping, int reconnecting) =>
        Pills(
            (PcTileState.Receiving, receiving, Strings.Pill_Receiving),
            (PcTileState.DroppingFrames, dropping, Strings.Pill_Dropping),
            (PcTileState.Reconnecting, reconnecting, Strings.Pill_Reconnecting));

    public static string SelectedLine(int selectedAndOnline, int all) =>
        Phrase.Format(Strings.Wall_Selected, ("selected", selectedAndOnline), ("all", all));

    public static string OfflineLine(IReadOnlyList<string> offlineNames) => offlineNames.Count switch
    {
        0 => string.Empty,
        1 => Phrase.Format(Strings.Wall_OfflineOne, ("name", offlineNames[0])),
        var n => Phrase.Format(Strings.Wall_OfflineMany, ("count", n)),
    };

    public static string OnAirLine(int receiving, IReadOnlyList<string> dropping, IReadOnlyList<string> reconnecting)
    {
        var allWell = dropping.Count == 0 && reconnecting.Count == 0;
        var parts = new List<string>
        {
            receiving switch
            {
                0 => Strings.Wall_ReceivingNone,
                > 1 when allWell => Phrase.Format(Strings.Wall_ReceivingAll, ("count", receiving)),
                _ => Phrase.Format(Strings.Wall_Receiving, ("count", receiving)),
            },
        };
        if (dropping.Count > 0)
        {
            parts.Add(dropping.Count == 1
                ? Phrase.Format(Strings.Wall_DroppingOne, ("name", dropping[0]))
                : Phrase.Format(Strings.Wall_DroppingMany, ("count", dropping.Count)));
        }

        if (reconnecting.Count > 0)
        {
            parts.Add(reconnecting.Count == 1
                ? Phrase.Format(Strings.Wall_ReconnectingOne, ("name", reconnecting[0]))
                : Phrase.Format(Strings.Wall_ReconnectingMany, ("count", reconnecting.Count)));
        }

        return string.Join(' ', parts);
    }

    /// <summary><c>m:ss</c> under an hour, <c>h:mm:ss</c> from an hour.</summary>
    public static string Elapsed(TimeSpan elapsed) =>
        elapsed.TotalHours >= 1
            ? $"{(int)elapsed.TotalHours}:{elapsed.Minutes:00}:{elapsed.Seconds:00}"
            : $"{(int)elapsed.TotalMinutes}:{elapsed.Seconds:00}";

    public static string DetailSentence(PcTileState state) => state switch
    {
        PcTileState.DroppingFrames => Strings.Detail_Dropping,
        PcTileState.Reconnecting => Strings.Detail_Reconnecting,
        PcTileState.Offline => Strings.Detail_Offline,
        _ => string.Empty,
    };

    public static string SharpnessText(Sharpness sharpness, int bitsPerSecond, CultureInfo culture) =>
        Phrase.Format(Strings.Wall_WithRate, ("label", Name(sharpness)), ("rate", Mbit(bitsPerSecond, culture)));

    public static string QualityButton(string presetLabel, int bitsPerSecond, CultureInfo culture) =>
        Phrase.Format(Strings.Wall_WithRate, ("label", presetLabel), ("rate", Mbit(bitsPerSecond, culture)));

    /// <summary>What the broadcast costs the network, for <paramref name="pcs"/> PCs selected and online.</summary>
    public static string NetworkLoad(int bitsPerSecond, int pcs, CultureInfo culture)
    {
        var rate = Mbit(bitsPerSecond, culture);
        return pcs switch
        {
            0 => string.Empty,
            1 => Phrase.Format(Strings.Wall_LoadOne, ("rate", rate)),
            _ => Phrase.Format(
                Strings.Wall_LoadMany,
                ("rate", rate),
                ("total", ((long)bitsPerSecond * pcs / 1_000_000.0).ToString("0", culture)),
                ("count", pcs)),
        };
    }

    // What a screen reader hears.

    public static string SelectBoxName(string pcName) => Phrase.Format(Strings.Wall_SelectName, ("name", pcName));

    public static string DetailsButtonName(string pcName) => Phrase.Format(Strings.Wall_DetailsName, ("name", pcName));

    public static string TileName(string pcName, string statusText) => $"{pcName}, {statusText}";

    public static string QualityPickerName(string qualityButtonText) => $"{Strings.Quality}, {qualityButtonText}";

    private static string Mbit(int bitsPerSecond, CultureInfo culture) => (bitsPerSecond / 1_000_000.0).ToString("0.0", culture);

    private static string Name(Sharpness sharpness) => sharpness switch
    {
        Sharpness.Light => Strings.Sharpness_Light,
        Sharpness.Sharp => Strings.Sharpness_Sharp,
        _ => Strings.Sharpness_Standard,
    };

    // The word after a pill's count depends on the count in some languages, so each is a plural text.
    private static IReadOnlyList<WallPill> Pills(params (PcTileState State, int Count, string Word)[] pills) =>
        pills.Where(p => p.Count > 0)
            .Select(p => new WallPill(p.State, p.Count, Phrase.Format(p.Word, ("count", p.Count))))
            .ToList();
}
