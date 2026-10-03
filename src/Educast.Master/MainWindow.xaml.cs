using System.Runtime.InteropServices;
using Educast.Master.Core;
using Educast.Master.Pages;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Graphics;

namespace Educast.Master;

/// <summary>
/// The console's one window: Mica, the title bar, the fault bar, and the <see cref="Frame"/>
/// that holds the two pages. It opens on the room page.
/// </summary>
public sealed partial class MainWindow : Window
{
    private const int DefaultWidth = 1000;
    private const int DefaultHeight = 640;
    private const int MinimumWidth = 760;
    private const int MinimumHeight = 520;

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

        RootFrame.Navigate(typeof(RoomPage), null, new SuppressNavigationTransitionInfo());
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
