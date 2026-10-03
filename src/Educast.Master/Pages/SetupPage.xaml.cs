using Educast.Master.Core.Setup;
using Educast.Protocol.Pairing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;

namespace Educast.Master.Pages;

/// <summary>
/// Set up devices. Everything it shows comes from <see cref="SetupViewModel"/>; this file only
/// binds, forwards clicks, and opens the three dialogs.
/// </summary>
public sealed partial class SetupPage : Page
{
    public SetupPage()
    {
        var services = App.Services;
        ViewModel = new SetupViewModel(
            services.Pairing,
            services.Discovery,
            services.RoomStore,
            services.Health,
            () => DateOnly.FromDateTime(DateTime.Now),
            action => DispatcherQueue.TryEnqueue(() => action()));

        InitializeComponent();
    }

    public SetupViewModel ViewModel { get; }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ViewModel.Activate();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        ViewModel.Deactivate();
        base.OnNavigatedFrom(e);
    }

    // The banner, as the InfoBar needs it.

    public bool HasValue(object? value) => value is not null;

    /// <summary>Go to This room is offered on the success bar only.</summary>
    public Visibility GoToRoomVisibility(SetupBanner? banner) =>
        banner?.Kind == SetupBannerKind.Success ? Visibility.Visible : Visibility.Collapsed;

    public InfoBarSeverity SeverityOf(SetupBanner? banner) =>
        banner?.Kind == SetupBannerKind.Success ? InfoBarSeverity.Success : InfoBarSeverity.Warning;

    public string TitleOf(SetupBanner? banner) => banner?.Title ?? string.Empty;

    public string MessageOf(SetupBanner? banner) => banner?.Message ?? string.Empty;

    // Navigation and the code card.

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (Frame.CanGoBack)
        {
            Frame.GoBack();
        }
    }

    private async void PairAll_Click(object sender, RoutedEventArgs e) => await ViewModel.PairAllAsync();

    private void Cancel_Click(object sender, RoutedEventArgs e) => ViewModel.Cancel();

    // Row actions. Each button sits inside the row's template, so its DataContext is the row;
    // the flyout items carry the row in their Tag because a flyout is not in the row's tree.

    private static SetupRow? RowOf(object sender) =>
        (sender as FrameworkElement)?.DataContext as SetupRow ?? (sender as FrameworkElement)?.Tag as SetupRow;

    private async void Pair_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } row)
        {
            await ViewModel.PairOneAsync(row);
        }
    }

    private async void TryAgain_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } row)
        {
            await ViewModel.PairOneAsync(row);
        }
    }

    private async void EnterCode_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is not { } row)
        {
            return;
        }

        var box = new PasswordBox
        {
            MaxLength = 7,
            PlaceholderText = "6 digits",
            PasswordRevealMode = PasswordRevealMode.Peek,
            InputScope = NumericPinScope(),
        };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"Room code for {row.Name}",
            Content = box,
            PrimaryButtonText = "Pair",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = false,
        };
        box.PasswordChanged += (_, _) => dialog.IsPrimaryButtonEnabled = RoomCode.TryNormalize(box.Password, out _);

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            await ViewModel.PairOneAsync(row, box.Password);
        }
    }

    private async void Rename_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is not { } row)
        {
            return;
        }

        var box = new TextBox { MaxLength = 64, Text = row.Name };
        box.SelectAll();
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"Rename {row.Name}",
            Content = box,
            PrimaryButtonText = "Rename",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            ViewModel.Rename(row, box.Text);
        }
    }

    private async void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is not { } row)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"Remove {row.Name} from this room?",
            Content = "It stops receiving your broadcasts. To add it back you will need the room code.",
            PrimaryButtonText = "Remove",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            ViewModel.Remove(row);
        }
    }

    // The list card draws its own border; the first row must not draw a second line under it.

    private void Rows_ElementPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args) =>
        ShowSeparator(args.Element, args.Index);

    private void Rows_ElementIndexChanged(ItemsRepeater sender, ItemsRepeaterElementIndexChangedEventArgs args) =>
        ShowSeparator(args.Element, args.NewIndex);

    private static void ShowSeparator(UIElement element, int index)
    {
        if (element is FrameworkElement row && row.FindName("RowSeparator") is UIElement separator)
        {
            separator.Visibility = index == 0 ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    private static InputScope NumericPinScope()
    {
        var scope = new InputScope();
        scope.Names.Add(new InputScopeName(InputScopeNameValue.NumericPin));
        return scope;
    }
}
