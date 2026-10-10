using System.Diagnostics;
using System.Runtime.InteropServices;
using Promin.Master.Core;
using Promin.Master.Pages;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Graphics;

namespace Promin.Master;

/// <summary>
/// The console's one window: Mica, the title bar, the fault bar, and the <c>Frame</c>
/// that holds the two pages. It opens on the room page.
/// </summary>
public sealed partial class MainWindow : Window
{
    private const int DefaultWidth = 1000;
    private const int DefaultHeight = 640;
    private const int MinimumWidth = 760;
    private const int MinimumHeight = 520;

    private readonly string? _languageAtStart;

    public MainWindow(ConsoleHealth health)
    {
        ArgumentNullException.ThrowIfNull(health);
        Health = health;

        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        // The taskbar and Alt+Tab icon. SetIcon resolves a relative path against the current
        // directory, which is not the app's when it is started from elsewhere.
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        SizeInEffectivePixels();

        _languageAtStart = App.Services.WallSettings.Language;
        foreach (var item in LanguageMenu.Items.OfType<RadioMenuFlyoutItem>())
        {
            item.IsChecked = (string?)item.Tag == _languageAtStart;
        }

        RootFrame.Navigate(typeof(RoomPage), null, new SuppressNavigationTransitionInfo());
    }

    /// <summary>True when the person asked for a restart; the app starts again once this window has closed.</summary>
    public bool RestartRequested { get; private set; }

    // A language holds for a whole run, so a new one needs a restart: now, or whenever Promin next starts.
    private async void OnLanguageChosen(object sender, RoutedEventArgs e)
    {
        var language = (string?)((RadioMenuFlyoutItem)sender).Tag;
        App.Services.WallSettings.Language = language;
        Trace.TraceInformation("Language chosen: {0}.", language ?? "the Windows language");
        if (language == _languageAtStart)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = Strings.Language_RestartTitle,
            Content = Strings.Language_RestartText,
            PrimaryButtonText = Strings.Language_RestartNow,
            CloseButtonText = Strings.Later,
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            RestartRequested = true;
            Close();
        }
    }

    public ConsoleHealth Health { get; }

    /// <summary>
    /// The sizes in the design are effective pixels; AppWindow takes physical ones, so both are
    /// scaled by the DPI of the monitor the window opens on.
    /// </summary>
    private void SizeInEffectivePixels()
    {
        var scale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
        AppWindow.ResizeClient(new SizeInt32((int)(DefaultWidth * scale), (int)(DefaultHeight * scale)));

        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = (int)(MinimumWidth * scale);
            presenter.PreferredMinimumHeight = (int)(MinimumHeight * scale);
        }
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);
}
