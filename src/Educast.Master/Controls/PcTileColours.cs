using Educast.Master.Core.Wall;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Educast.Master.Controls;

/// <summary>The design's colour-means-state table, for the dots the page draws itself (the pills), and the pill's two runs.</summary>
public static class PcTileColours
{
    public static Brush DotBrush(PcTileState state) => (Brush)Application.Current.Resources[state switch
    {
        PcTileState.Offline => "TextFillColorSecondaryBrush",
        PcTileState.DroppingFrames => "SystemFillColorCautionBrush",
        PcTileState.Reconnecting => "SystemFillColorCriticalBrush",
        _ => "SystemFillColorSuccessBrush",
    }];

    /// <summary>The count of a pill's text ("9 ready" gives "9 ").</summary>
    public static string CountOf(string pillText) => pillText[..(pillText.IndexOf(' ') + 1)];

    /// <summary>The word of a pill's text ("9 ready" gives "ready").</summary>
    public static string WordOf(string pillText) => pillText[(pillText.IndexOf(' ') + 1)..];
}
