using System.ComponentModel;
using Educast.Master.Core.Wall;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

namespace Educast.Master.Controls;

/// <summary>
/// One tile of the room wall, bound to a <see cref="PcTile"/>. Everything it shows comes from the
/// tile; it raises an event for each thing the teacher asks for and decides nothing itself.
/// </summary>
public sealed partial class PcTileControl : UserControl
{
    public static readonly DependencyProperty TileProperty = DependencyProperty.Register(
        nameof(Tile), typeof(PcTile), typeof(PcTileControl), new PropertyMetadata(null, OnTileChanged));

    private static readonly TimeSpan LightUpDuration = TimeSpan.FromMilliseconds(450);

    public PcTileControl()
    {
        InitializeComponent();
    }

    public PcTile? Tile
    {
        get => (PcTile?)GetValue(TileProperty);
        set => SetValue(TileProperty, value);
    }

    /// <summary>The teacher clicked the tile or its checkbox.</summary>
    public event Action<PcTile>? Toggled;

    public event Action<PcTile>? StopSendingRequested;

    public event Action<PcTile>? RenameRequested;

    public event Action<PcTile>? RemoveRequested;

    /// <summary>The moment a broadcast starts: the screen area fades and scales in, after <paramref name="delay"/>.</summary>
    public void PlayLightUp(TimeSpan delay)
    {
        var storyboard = new Storyboard { BeginTime = delay };
        storyboard.Children.Add(Animate(Screen, "Opacity", 0, 1));
        storyboard.Children.Add(Animate(ScreenScale, "ScaleX", 1.04, 1));
        storyboard.Children.Add(Animate(ScreenScale, "ScaleY", 1.04, 1));
        Screen.Opacity = 0;
        storyboard.Begin();
    }

    // The bindings' helpers.

    public Visibility Not(bool value) => value ? Visibility.Collapsed : Visibility.Visible;

    public Visibility NotEmpty(string value) => string.IsNullOrEmpty(value) ? Visibility.Collapsed : Visibility.Visible;

    public Visibility IsBeingSentTo(PcTileState state) =>
        state is PcTileState.Receiving or PcTileState.DroppingFrames ? Visibility.Visible : Visibility.Collapsed;

    public string FramesText(int dropped, int offered) => $"{dropped:N0} of {offered:N0}";

    private static Timeline Animate(DependencyObject target, string property, double from, double to)
    {
        var animation = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = new Duration(LightUpDuration),
        };
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, property);
        return animation;
    }

    private static void OnTileChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        var control = (PcTileControl)sender;
        if (e.OldValue is PcTile old)
        {
            old.PropertyChanged -= control.Tile_PropertyChanged;
        }

        if (e.NewValue is PcTile tile)
        {
            tile.PropertyChanged += control.Tile_PropertyChanged;
            control.ShowState(tile);
        }

        // The template gives the control its tile after the bindings were made.
        control.Bindings.Update();
    }

    private void Tile_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PcTile.State) or nameof(PcTile.IsSelected))
        {
            ShowState((PcTile)sender!);
        }
    }

    private void ShowState(PcTile tile)
    {
        VisualStateManager.GoToState(this, tile.State.ToString(), useTransitions: false);
        VisualStateManager.GoToState(this, tile.IsSelected ? "Selected" : "Unselected", useTransitions: false);
    }

    // 16:10, whatever width the grid gives the tile.
    private void Screen_SizeChanged(object sender, SizeChangedEventArgs e) => Screen.Height = e.NewSize.Width * 10 / 16;

    // A tap anywhere on the card toggles, except on the checkbox and the More button, which answer for themselves.
    private void Card_Tapped(object sender, TappedRoutedEventArgs e)
    {
        for (var element = e.OriginalSource as DependencyObject; element is not null && element != Card; element = VisualTreeHelper.GetParent(element))
        {
            if (element == SelectBox || element == More)
            {
                return;
            }
        }

        RaiseToggled();
    }

    private void SelectBox_Click(object sender, RoutedEventArgs e)
    {
        RaiseToggled();

        // The click flipped the box; the tile decided (a failed save keeps the stored state), so the box shows the tile.
        SelectBox.IsChecked = Tile?.IsSelected;
    }

    private void RaiseToggled()
    {
        if (Tile is { } tile)
        {
            Toggled?.Invoke(tile);
        }
    }

    private void StopSending_Click(object sender, RoutedEventArgs e) => Ask(StopSendingRequested);

    private void Rename_Click(object sender, RoutedEventArgs e) => Ask(RenameRequested);

    private void Remove_Click(object sender, RoutedEventArgs e) => Ask(RemoveRequested);

    private void Ask(Action<PcTile>? request)
    {
        Details.Hide();
        if (Tile is { } tile)
        {
            request?.Invoke(tile);
        }
    }
}
