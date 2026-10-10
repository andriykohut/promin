using System.ComponentModel;
using System.Diagnostics;
using Promin.Master.Core;
using Promin.Master.Core.Setup;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;

namespace Promin.Master.Pages;

/// <summary>
/// Set up devices. Everything it shows comes from <see cref="SetupViewModel"/>; this file only
/// binds, forwards clicks and the code box's content, shows and announces the code hint, and
/// opens the three dialogs.
/// </summary>
/// <remarks>
/// No <c>async void</c> handler here lets an exception escape: the view model never throws for
/// anything a disk, the network or a student PC does, and whatever is left (a programming error,
/// a second dialog opened over the first) is written to the debug output rather than ending the
/// process from inside the dispatcher.
/// </remarks>
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
            services.Broadcast,
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

    // The one bindings' helper; its argument is a value type, because a function over a null object is never re-run.

    public InfoBarSeverity SeverityOf(SetupBannerKind kind) => kind switch
    {
        SetupBannerKind.Success => InfoBarSeverity.Success,
        SetupBannerKind.Error => InfoBarSeverity.Error,
        _ => InfoBarSeverity.Warning,
    };

    // The room code field: a stock PasswordBox. Its content is forwarded to CodeText as it is;
    // the one write back is the view model clearing the code when every PC is in the room (the
    // box already holds what was typed, so nothing is written in reaction to typing).

    private void CodeBox_PasswordChanged(object sender, RoutedEventArgs e) => ViewModel.CodeText = CodeBox.Password;

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(SetupViewModel.CodeText) when CodeBox.Password != ViewModel.CodeText:
                CodeBox.Password = ViewModel.CodeText;
                break;
            case nameof(SetupViewModel.ShowsCodeHint):
                ShowHint(CodeHint, ViewModel.ShowsCodeHint);
                break;
        }
    }

    /// <summary>
    /// Shows or hides a code hint without moving anything: the hint keeps its line and is hidden
    /// by opacity, leaving the automation tree as it goes. It is a polite live region, and the
    /// change event is raised exactly when it goes from hidden to shown — once, not on every
    /// keystroke while it stays — after the frame in which it became visible.
    /// </summary>
    private void ShowHint(TextBlock hint, bool shown)
    {
        var wasShown = hint.Opacity > 0;
        hint.Opacity = shown ? 1 : 0;
        AutomationProperties.SetAccessibilityView(hint, shown ? AccessibilityView.Content : AccessibilityView.Raw);

        if (shown && !wasShown)
        {
            DispatcherQueue.TryEnqueue(() =>
                FrameworkElementAutomationPeer.CreatePeerForElement(hint)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged));
        }
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
            // The same stock box and hint as the card, driven by EnterCodeViewModel's change
            // notifications; the hint keeps its line below the box whether or not it shows.
            // Enter is the dialog's default button, which is enabled only while the code is valid.
            var code = new EnterCodeViewModel();
            var box = new PasswordBox
            {
                PlaceholderText = Strings.Setup_CodePlaceholder,
                PasswordRevealMode = PasswordRevealMode.Peek,
                InputScope = NumericPinScope(),
            };
            AutomationProperties.SetName(box, Strings.RoomCode);
            var hint = new TextBlock
            {
                Text = EnterCodeViewModel.HintText,
                Style = (Style)Resources["SecondaryTextStyle"],
                Margin = new Thickness(0, 4, 0, 0),
                Opacity = 0,
                IsHitTestVisible = false,
            };
            AutomationProperties.SetAccessibilityView(hint, AccessibilityView.Raw);
            AutomationProperties.SetLiveSetting(hint, AutomationLiveSetting.Polite);
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = Phrase.Format(Strings.Dialog_CodeTitle, ("name", row.Name)),
                Content = new StackPanel { Children = { box, hint } },
                PrimaryButtonText = Strings.Pair,
                CloseButtonText = Strings.Cancel,
                DefaultButton = ContentDialogButton.Primary,
                IsPrimaryButtonEnabled = false,
            };
            box.PasswordChanged += (_, _) => code.CodeText = box.Password;
            code.PropertyChanged += (_, change) =>
            {
                switch (change.PropertyName)
                {
                    case nameof(EnterCodeViewModel.ShowsHint):
                        ShowHint(hint, code.ShowsHint);
                        break;
                    case nameof(EnterCodeViewModel.CanPair):
                        dialog.IsPrimaryButtonEnabled = code.CanPair;
                        break;
                }
            };

            if (await dialog.ShowAsync() == ContentDialogResult.Primary && code.Code is { } sixDigits)
            {
                await ViewModel.PairOneAsync(row, sixDigits);
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
            if (await RoomDialogs.RenameAsync(XamlRoot, row.Name) is { } newName)
            {
                ViewModel.Rename(row, newName);
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
            if (await RoomDialogs.ConfirmRemoveAsync(XamlRoot, row.Name))
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
