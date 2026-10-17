using Microsoft.UI.Xaml;

namespace Promin.Agent.Window;

/// <summary>The agent window: one window, nothing else.</summary>
public partial class App : Application
{
    private MainWindow? _window;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        _window.Activate();
    }
}
