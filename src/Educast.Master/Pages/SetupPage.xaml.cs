using System.ComponentModel;
using System.Diagnostics;
using Educast.Master.Core.Setup;
using Educast.Protocol.Pairing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;

namespace Educast.Master.Pages;

/// <summary>
/// Set up devices. Everything it shows comes from <see cref="SetupViewModel"/>; this file only
/// binds, forwards clicks, filters the code field, and opens the three dialogs.
/// </summary>
/// <remarks>
/// No <c>async void</c> handler here lets an exception escape: the view model never throws for
/// anything a disk, the network or a student PC does, and whatever is left (a programming error,
/// a second dialog opened over the first) is written to the debug output rather than ending the
/// process from inside the dispatcher.
/// </remarks>
public sealed partial class SetupPage : Page
{
    private bool _settingCode;

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
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
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

    // The banners, as an InfoBar needs them.

    public bool HasValue(object? value) => value is not null;

    /// <summary>Go to This room is offered on the success bar only.</summary>
    public Visibility GoToRoomVisibility(SetupBanner? banner) =>
        banner?.Kind == SetupBannerKind.Success ? Visibility.Visible : Visibility.Collapsed;

    public InfoBarSeverity SeverityOf(SetupBanner? banner) => banner?.Kind switch
    {
        SetupBannerKind.Success => InfoBarSeverity.Success,
        SetupBannerKind.Error => InfoBarSeverity.Error,
        _ => InfoBarSeverity.Warning,
    };

    public string TitleOf(SetupBanner? banner) => banner?.Title ?? string.Empty;

    public string MessageOf(SetupBanner? banner) => banner?.Message ?? string.Empty;

    // The room code field: six digits and nothing else (design doc 6.2).
    //
    // The field is not bound two-way. Every change the control reports is filtered through
    // RoomCodeEntry.Accept and the result pushed to the view model; the control's content is
    // set from code only when the filter refuses an input, and when the view model clears the
    // code (every PC in the room). Setting a PasswordBox's content from code hides its reveal
    // button until the next keystroke, so it is done as rarely as possible.

    private void CodeBox_PasswordChanged(object sender, RoutedEventArgs e) =>
        ViewModel.CodeText = FilterCode(CodeBox, ViewModel.CodeText);

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SetupViewModel.CodeText) && CodeBox.Password != ViewModel.CodeText)
        {
            SetPassword(CodeBox, ViewModel.CodeText);
        }
    }

    /// <summary>Applies <see cref="RoomCodeEntry.Accept"/> to a change in <paramref name="box"/> and returns what it now holds.</summary>
    private string FilterCode(PasswordBox box, string current)
    {
        if (_settingCode)
        {
            return box.Password;
        }

        var accepted = RoomCodeEntry.Accept(current, box.Password);
        if (accepted != box.Password)
        {
            SetPassword(box, accepted);
        }

        return accepted;
    }

    private void SetPassword(PasswordBox box, string value)
    {
        _settingCode = true;
        try
        {
            box.Password = value;
        }
        finally
        {
            _settingCode = false;
        }
    }

    private PasswordBox NewCodeBox()
    {
        var box = new PasswordBox
        {
            MaxLength = RoomCode.Length,
            PlaceholderText = "6 digits",
            PasswordRevealMode = PasswordRevealMode.Peek,
            InputScope = NumericPinScope(),
        };
        var current = string.Empty;
        box.PasswordChanged += (_, _) => current = FilterCode(box, current);
        return box;
    }

    // Navigation and the code card.

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (Frame.CanGoBack)
        {
            Frame.GoBack(new SuppressNavigationTransitionInfo());
        }
    }

    private async void PairAll_Click(object sender, RoutedEventArgs e) => await Guarded(ViewModel.PairAllAsync);

    private void Cancel_Click(object sender, RoutedEventArgs e) => ViewModel.Cancel();

    // Row actions. Each button sits inside the row's template, so its DataContext is the row;
    // the flyout items carry the row in their Tag because a flyout is not in the row's tree.

    private static SetupRow? RowOf(object sender) =>
        (sender as FrameworkElement)?.DataContext as SetupRow ?? (sender as FrameworkElement)?.Tag as SetupRow;

    /// <summary>Pair, and Try again: the row with the main field's code.</summary>
    private async void Pair_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } row)
        {
            await Guarded(() => ViewModel.PairOneAsync(row));
        }
    }

    private async void EnterCode_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is not { } row)
        {
            return;
        }

        await Guarded(async () =>
        {
            var box = NewCodeBox();
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
            box.PasswordChanged += (_, _) => dialog.IsPrimaryButtonEnabled = RoomCode.IsValid(box.Password);

            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                await ViewModel.PairOneAsync(row, box.Password);
            }
        });
    }

    private async void Rename_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is not { } row)
        {
            return;
        }

        await Guarded(async () =>
        {
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
        });
    }

    private async void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is not { } row)
        {
            return;
        }

        await Guarded(async () =>
        {
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
        });
    }

    /// <summary>Runs a handler's work so that nothing it throws reaches the dispatcher.</summary>
    private static async Task Guarded(Func<Task> work)
    {
        try
        {
            await work();
        }
        catch (Exception e)
        {
            Trace.TraceError("Set up devices: a handler failed unexpectedly: {0}", e);
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
