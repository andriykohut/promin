using System.ComponentModel;
using System.Diagnostics;
using Promin.Master.Controls;
using Promin.Master.Core.Sending;
using Promin.Master.Core.Wall;
using Promin.Protocol.Video;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using Windows.UI.ViewManagement;

namespace Promin.Master.Pages;

/// <summary>
/// The room wall. Everything it shows comes from <see cref="RoomWallViewModel"/> and, for a send,
/// <see cref="RoomSend"/>; this file only binds, forwards clicks, ticks them once a second, opens the
/// dialogs, and plays the one animation the design allows. The page is kept across navigation so the
/// wall, and a running broadcast or send, survive a visit to Set up devices.
/// </summary>
public sealed partial class RoomPage : Page
{
    private static readonly TimeSpan LightUpStagger = TimeSpan.FromMilliseconds(45);

    private readonly DispatcherQueueTimer _timer;
    private readonly UISettings _uiSettings = new();
    private readonly Storyboard _pulse = new() { RepeatBehavior = RepeatBehavior.Forever, AutoReverse = true };

    public RoomPage()
    {
        var services = App.Services;
        ViewModel = new RoomWallViewModel(
            services.RoomStore,
            services.Discovery,
            services.Broadcast,
            services.Connections,
            services.Pairing,
            services.WallSettings,
            () => DateTimeOffset.Now,
            action => DispatcherQueue.TryEnqueue(() => action()));
        Sending = new RoomSend(
            services.Files,
            deviceId => ViewModel.Tiles.FirstOrDefault(t => t.DeviceId == deviceId),
            () => DateTimeOffset.Now,
            action => DispatcherQueue.TryEnqueue(() => action()));

        InitializeComponent();

        _timer = DispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(1);
        _timer.Tick += (_, _) =>
        {
            ViewModel.Tick();
            Sending.Tick();
        };

        var fade = new DoubleAnimation { From = 1, To = 0.3, Duration = new Duration(TimeSpan.FromMilliseconds(900)) };
        Storyboard.SetTarget(fade, Pulse);
        Storyboard.SetTargetProperty(fade, "Opacity");
        _pulse.Children.Add(fade);

        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
    }

    public RoomWallViewModel ViewModel { get; }

    public RoomSend Sending { get; }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ViewModel.Activate();
        _timer.Start();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        _timer.Stop();
        base.OnNavigatedFrom(e);
    }

    // The bindings' helpers. Every argument is a value type: a function over a null object is never re-run.

    public Visibility Not(bool value) => value ? Visibility.Collapsed : Visibility.Visible;

    public Visibility SelectionLinksVisibility(bool onAir, bool emptyRoom) => onAir || emptyRoom ? Visibility.Collapsed : Visibility.Visible;

    public Visibility SeparatorVisibility(bool onAir, bool emptyRoom, int line2Length) =>
        line2Length > 0 ? SelectionLinksVisibility(onAir, emptyRoom) : Visibility.Collapsed;

    public InfoBarSeverity SeverityOf(WallMessageKind kind) => kind switch
    {
        WallMessageKind.Error => InfoBarSeverity.Error,
        WallMessageKind.Warning => InfoBarSeverity.Warning,
        _ => InfoBarSeverity.Informational,
    };

    public InfoBarSeverity StripSeverity(SendStripKind kind) => kind switch
    {
        SendStripKind.SomeMissed => InfoBarSeverity.Warning,
        SendStripKind.AllSent => InfoBarSeverity.Success,
        SendStripKind.SourceFailed => InfoBarSeverity.Error,
        _ => InfoBarSeverity.Informational,
    };

    public bool CanSendFiles(bool sending, bool emptyRoom) => !sending && !emptyRoom;

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(RoomWallViewModel.IsOnAir))
        {
            return;
        }

        if (!ViewModel.IsOnAir)
        {
            _pulse.Stop();
            return;
        }

        if (!_uiSettings.AnimationsEnabled)
        {
            return;
        }

        _pulse.Begin();
        for (var i = 0; i < ViewModel.Tiles.Count; i++)
        {
            if (WallRepeater.TryGetElement(i) is PcTileControl { Tile.State: PcTileState.Receiving } tile)
            {
                tile.PlayLightUp(LightUpStagger * i);
            }
        }
    }

    // Navigation and the bar.

    // No slide: the design allows no decorative animation on these screens.
    private void SetUpDevices_Click(object sender, RoutedEventArgs e) =>
        Frame.Navigate(typeof(SetupPage), null, new SuppressNavigationTransitionInfo());

    private async void Start_Click(object sender, RoutedEventArgs e) => await Guarded(ViewModel.StartAsync);

    private void Stop_Click(object sender, RoutedEventArgs e) => ViewModel.Stop();

    private void SelectAll_Click(object sender, RoutedEventArgs e) => ViewModel.SelectAll();

    private void Clear_Click(object sender, RoutedEventArgs e) => ViewModel.ClearSelection();

    // The send: the window, then the strip's button and X.

    private async void SendFiles_Click(object sender, RoutedEventArgs e) => await Guarded(async () =>
    {
        var dialog = new SendFilesDialog(ViewModel.SendTargets()) { XamlRoot = XamlRoot };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary && dialog.Package is { } package)
        {
            Sending.Start(package, dialog.Model.OpenOnArrival, dialog.Model.Chosen);
        }
    });

    private void StripAction_Click(object sender, RoutedEventArgs e) => Sending.StripAction();

    private void SendStrip_CloseButtonClick(InfoBar sender, object args) => Sending.CloseStrip();

    // The picker: the two groups show the view model's choice when it opens and hand a change back.

    private void Picker_Opening(object sender, object e)
    {
        QualityRadios.SelectedItem = ViewModel.SelectedPreset;
        SharpnessRadios.SelectedItem = ViewModel.SelectedSharpness;
    }

    private void QualityRadios_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (QualityRadios.SelectedItem is QualityPreset preset)
        {
            ViewModel.SelectedPreset = preset;

            // The sharpness rows were rebuilt with the new rates; the chosen one is shown again.
            SharpnessRadios.SelectedItem = ViewModel.SelectedSharpness;
        }
    }

    private void SharpnessRadios_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SharpnessRadios.SelectedItem is SharpnessChoice choice)
        {
            ViewModel.SelectedSharpness = choice;
        }
    }

    // The tiles: each raises what the presenter asked for; the page answers through the view model.

    private void Wall_ElementPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args)
    {
        if (args.Element is PcTileControl tile)
        {
            tile.Toggled += ViewModel.ToggleSelected;
            tile.StopSendingRequested += ViewModel.StopSendingTo;
            tile.RenameRequested += Rename;
            tile.RemoveRequested += Remove;
        }
    }

    private void Wall_ElementClearing(ItemsRepeater sender, ItemsRepeaterElementClearingEventArgs args)
    {
        if (args.Element is PcTileControl tile)
        {
            tile.Toggled -= ViewModel.ToggleSelected;
            tile.StopSendingRequested -= ViewModel.StopSendingTo;
            tile.RenameRequested -= Rename;
            tile.RemoveRequested -= Remove;
        }
    }

    private async void Rename(PcTile tile) => await Guarded(async () =>
    {
        if (await RoomDialogs.RenameAsync(XamlRoot, tile.Name) is { } newName)
        {
            ViewModel.Rename(tile, newName);
        }
    });

    private async void Remove(PcTile tile) => await Guarded(async () =>
    {
        if (await RoomDialogs.ConfirmRemoveAsync(XamlRoot, tile.Name))
        {
            ViewModel.Remove(tile);
        }
    });

    /// <summary>Runs a handler's work so that nothing it throws reaches the dispatcher.</summary>
    private static async Task Guarded(Func<Task> work)
    {
        try
        {
            await work();
        }
        catch (Exception e)
        {
            Trace.TraceError("This room: a handler failed unexpectedly: {0}", e);
        }
    }
}
