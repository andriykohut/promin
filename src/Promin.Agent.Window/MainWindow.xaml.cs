using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Promin.Agent.Core;
using Windows.Graphics;
using Windows.System;

namespace Promin.Agent.Window;

/// <summary>
/// The agent's one window: the room code box with Save beside it, and one bar that says whether
/// this PC is paired. The files are re-read every two seconds, so pairing from the console shows
/// up here without a restart.
/// </summary>
public sealed partial class MainWindow : Microsoft.UI.Xaml.Window
{
    private const int Width = 420;
    private const int Height = 300;

    private readonly AgentWindowModel _model = new(AgentPaths.ForThisMachine());
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };

    public MainWindow()
    {
        InitializeComponent();

        Title = Strings.Window_Title;
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        // The taskbar and Alt+Tab icon. SetIcon resolves a relative path against the current
        // directory, which is not the app's when it is started from elsewhere.
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        SizeInEffectivePixels();

        _model.Refresh();
        CodeBox.Text = _model.StoredCode ?? "";
        Render();

        // The timer never writes to the box: a code being typed must not be overwritten.
        _timer.Tick += (_, _) =>
        {
            _model.Refresh();
            Render();
        };
        _timer.Start();
        Closed += (_, _) => _timer.Stop();
    }

    private void CodeBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _model.Edited();
        Render();
    }

    private void CodeBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            Save();
            e.Handled = true;
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e) => Save();

    private void Save()
    {
        if (_model.Save(CodeBox.Text))
        {
            CodeBox.Text = _model.StoredCode ?? "";
        }

        Render();
    }

    // Save is the accent button while the box holds something other than the stored code.
    private void Render()
    {
        StatusBar.Severity = _model.Severity switch
        {
            StripSeverity.Success => InfoBarSeverity.Success,
            StripSeverity.Error => InfoBarSeverity.Error,
            _ => InfoBarSeverity.Informational,
        };
        StatusBar.Message = _model.Message;
        SaveButton.Style = CodeBox.Text != _model.StoredCode
            ? (Style)Application.Current.Resources["AccentButtonStyle"]
            : null;
    }

    /// <summary>
    /// The sizes in the design are effective pixels; AppWindow takes physical ones, so both are
    /// scaled by the DPI of the monitor the window opens on.
    /// </summary>
    private void SizeInEffectivePixels()
    {
        var scale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
        AppWindow.ResizeClient(new SizeInt32((int)(Width * scale), (int)(Height * scale)));

        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
        }
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);
}
