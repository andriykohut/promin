using System.Diagnostics;
using Promin.Master.Core;
using Promin.Master.Core.Wall;
using Promin.Master.Services;
using Microsoft.UI.Xaml;

namespace Promin.Master;

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
        // First, so that whatever goes wrong after this is written down. Without a log the console starts anyway.
        if (ConsoleLog.TryOpen(AppServices.RootDirectory) is { } log)
        {
            Trace.Listeners.Add(log);
            Trace.AutoFlush = true;
        }

        Trace.TraceInformation("Promin console {0} started.", typeof(App).Assembly.GetName().Version);

        // Before anything builds a text: the language holds for the whole run.
        AppLanguage.Apply(new JsonWallSettings(AppServices.SettingsPath).Language);

        Services = AppServices.Create();

        var window = new MainWindow(Services.Health);
        window.Closed += (_, _) =>
        {
            Services.Dispose();
            if (window.RestartRequested)
            {
                Microsoft.Windows.AppLifecycle.AppInstance.Restart(string.Empty);
            }
        };
        _window = window;
        _window.Activate();

        _ = Services.CheckCertificateAsync(_window.DispatcherQueue);
    }
}
