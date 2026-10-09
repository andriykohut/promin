using System.Globalization;
using Educast.Protocol.Video;

namespace Educast.Master.Core.Wall;

/// <summary>Every string the room wall shows, from counts and names, so the wording is tested on its own.</summary>
public static class WallText
{
    public static string Status(PcTileState state, int framesDropped) => state switch
    {
        PcTileState.Ready => "Ready",
        PcTileState.Offline => "Offline",
        PcTileState.Receiving => "Receiving",
        PcTileState.DroppingFrames => framesDropped == 1 ? "1 frame dropped" : $"{framesDropped} frames dropped",
        PcTileState.Reconnecting => "Reconnecting",
        _ => string.Empty,
    };

    /// <summary>The header pills off air. A pill whose count is 0 is left out.</summary>
    public static IReadOnlyList<WallPill> PillsOffAir(int ready, int offline) =>
        Pills((PcTileState.Ready, ready, "ready"), (PcTileState.Offline, offline, "offline"));

    /// <summary>The header pills on air. A pill whose count is 0 is left out.</summary>
    public static IReadOnlyList<WallPill> PillsOnAir(int receiving, int dropping, int reconnecting) =>
        Pills(
            (PcTileState.Receiving, receiving, "receiving"),
            (PcTileState.DroppingFrames, dropping, "dropping frames"),
            (PcTileState.Reconnecting, reconnecting, "reconnecting"));

    public static string SelectedLine(int selectedAndOnline, int all) => $"{selectedAndOnline} of {all} PCs selected";

    public static string OfflineLine(IReadOnlyList<string> offlineNames) => offlineNames.Count switch
    {
        0 => string.Empty,
        1 => $"{offlineNames[0]} is offline. It joins by itself if it comes back.",
        var n => $"{n} PCs are offline. They join by themselves if they come back.",
    };

    public static string OnAirLine(int receiving, IReadOnlyList<string> dropping, IReadOnlyList<string> reconnecting)
    {
        if (dropping.Count == 0 && reconnecting.Count == 0)
        {
            return $"All {receiving} PCs receiving well.";
        }

        var parts = new List<string> { receiving == 1 ? "1 PC receiving well." : $"{receiving} PCs receiving well." };
        if (dropping.Count > 0)
        {
            parts.Add(dropping.Count == 1 ? $"{dropping[0]} is dropping frames." : $"{dropping.Count} PCs are dropping frames.");
        }

        if (reconnecting.Count > 0)
        {
            parts.Add(reconnecting.Count == 1 ? $"{reconnecting[0]} is reconnecting." : $"{reconnecting.Count} PCs are reconnecting.");
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
        PcTileState.DroppingFrames => "Its connection is slow. The other PCs are not affected.",
        PcTileState.Reconnecting => "It dropped off the network. It rejoins by itself when it is back.",
        PcTileState.Offline => "It is switched off or not on this network.",
        _ => string.Empty,
    };

    public static string SharpnessText(Sharpness sharpness, int bitsPerSecond, CultureInfo culture) =>
        $"{sharpness}, {Mbit(bitsPerSecond, culture)} Mbit/s";

    public static string QualityButton(string presetLabel, int bitsPerSecond, CultureInfo culture) =>
        $"{presetLabel}, {Mbit(bitsPerSecond, culture)} Mbit/s";

    /// <summary>What the broadcast costs the network, for <paramref name="pcs"/> PCs selected and online.</summary>
    public static string NetworkLoad(int bitsPerSecond, int pcs, CultureInfo culture)
    {
        var rate = Mbit(bitsPerSecond, culture);
        return pcs switch
        {
            0 => string.Empty,
            1 => $"{rate} Mbit/s to 1 PC.",
            _ => $"{rate} Mbit/s to each PC, about {((long)bitsPerSecond * pcs / 1_000_000.0).ToString("0", culture)} Mbit/s for {pcs} PCs.",
        };
    }

    private static string Mbit(int bitsPerSecond, CultureInfo culture) => (bitsPerSecond / 1_000_000.0).ToString("0.0", culture);

    private static IReadOnlyList<WallPill> Pills(params (PcTileState State, int Count, string Word)[] pills) =>
        pills.Where(p => p.Count > 0).Select(p => new WallPill(p.State, $"{p.Count} {p.Word}")).ToList();
}
