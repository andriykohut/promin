using System.Diagnostics;
using Promin.Master.Core;
using Promin.Master.Core.Wall;
using Promin.Master.Services;
using Promin.Protocol.Updates;
using Microsoft.UI.Dispatching;
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

        // Three sources, because each sees crashes the others do not: the UI thread, any other thread, and a
        // task nobody awaited. None of them stops the crash.
        UnhandledException += (_, e) => Trace.TraceError("Unhandled exception on the UI thread: {0}", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Trace.TraceError("Unhandled exception: {0}", e.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, e) =>
            Trace.TraceError("A task failed and nobody was waiting for it: {0}", e.Exception);

        Trace.TraceInformation("Promin console {0} started.", ReleaseVersion.Of(typeof(App).Assembly));

        // Before anything builds a text: the language holds for the whole run.
        AppLanguage.Apply(new JsonWallSettings(AppServices.SettingsPath).Language);

        Services = AppServices.Create();
        var arguments = Environment.GetCommandLineArgs();
        var restartArguments = string.Empty;
        var demo = false;
        ReleaseVersion? named = null;
#if DEBUG
        if (arguments.Contains("--demo"))
        {
            Services.UseDemoRoom();
            restartArguments = "--demo";
            demo = true;
        }

        // For hand tests before a release exists: treats the named release, a beta included, as the latest.
        var from = Array.IndexOf(arguments, "--update-from");
        if (from >= 0 && from + 1 < arguments.Length && ReleaseVersion.TryParse(arguments[from + 1], out var version))
        {
            named = version;
        }
#endif

        var ui = DispatcherQueue.GetForCurrentThread();
        Services.CreateUpdates(action => ui.TryEnqueue(() => action()));

        // The console's setup relaunches it with this once it has updated this PC: the noted PCs come next.
        if (arguments.Contains("--continue-update"))
        {
            Services.Updates.Continue();
        }

        var window = new MainWindow(Services.Health);
        Services.Updates.ExitRequested += window.Close;
        window.Closed += (_, _) =>
        {
            Services.Dispose();
            if (window.RestartRequested)
            {
                Microsoft.Windows.AppLifecycle.AppInstance.Restart(restartArguments);
            }
        };
        _window = window;
        _window.Activate();

        _ = Services.CheckCertificateAsync(_window.DispatcherQueue);

        // The made-up room is for screenshots: it shows no update.
        if (!demo)
        {
            _ = Services.Updates.CheckAsync(named);
        }
    }
}
