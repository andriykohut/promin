using Educast.Master.Core.Wall;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Educast.Master.Controls;

/// <summary>The design's colour-means-state table, for the dots the page draws itself (the pills).</summary>
public static class PcTileColours
{
    public static Brush DotBrush(PcTileState state) => (Brush)Application.Current.Resources[state switch
    {
        PcTileState.Offline => "TextFillColorSecondaryBrush",
        PcTileState.DroppingFrames => "SystemFillColorCautionBrush",
        PcTileState.Reconnecting => "SystemFillColorCriticalBrush",
        _ => "SystemFillColorSuccessBrush",
    }];
}
