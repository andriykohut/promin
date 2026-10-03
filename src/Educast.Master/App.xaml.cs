using Educast.Master.Services;
using Microsoft.UI.Xaml;

namespace Educast.Master;

/// <summary>The console. One window, one <see cref="AppServices"/> for its whole life.</summary>
public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();
    }

    /// <summary>What the pages share. Built in <see cref="OnLaunched"/>, before any page exists.</summary>
    public static AppServices Services { get; private set; } = null!;

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Services = AppServices.Create();

        _window = new MainWindow(Services.Health);
        _window.Closed += (_, _) => Services.Dispose();
        _window.Activate();

        _ = Services.CheckCertificateAsync(_window.DispatcherQueue);
    }
}
