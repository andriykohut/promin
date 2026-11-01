using Promin.Master.Core.Wall;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Promin.Master.Controls;

/// <summary>
/// The design's colour-means-state table, for the dots the page draws itself (the pills), and which
/// states show a lock glyph in the dot's place.
/// </summary>
public static class PcTileColours
{
    public static Brush DotBrush(PcTileState state) => (Brush)Application.Current.Resources[state switch
    {
        PcTileState.Offline => "TextFillColorSecondaryBrush",
        PcTileState.DroppingFrames => "SystemFillColorCautionBrush",
        PcTileState.Reconnecting => "SystemFillColorCriticalBrush",
        _ => "SystemFillColorSuccessBrush",
    }];

    public static Visibility DotVisibility(PcTileState state) => state == PcTileState.Locked ? Visibility.Collapsed : Visibility.Visible;

    public static Visibility LockVisibility(PcTileState state) => state == PcTileState.Locked ? Visibility.Visible : Visibility.Collapsed;
}
