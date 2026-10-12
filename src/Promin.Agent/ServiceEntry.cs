using System.ServiceProcess;

namespace Promin.Agent;

internal enum ServiceSessionEvent { SignedInOrConnected, SignedOutOrDisconnected }

internal readonly record struct ServiceSessionChange(ServiceSessionEvent Event, int SessionId);

/// <summary>
/// The two entry points for the Windows service: the guard that keeps <c>--service</c> from being run by hand,
/// and the host that runs the supervisor. The guard is unit-tested; the host is proven by the hand-tests.
/// </summary>
internal static class ServiceEntry
{
    /// <summary>-1 means "carry on and start the service"; any other value is the exit code the process should return instead.</summary>
    public static int RefuseIfInteractive(bool interactive, TextWriter output)
    {
        if (!interactive)
        {
            return -1;
        }

        output.WriteLine("--service is started by the Windows service manager, not from a terminal. Use --install first.");
        return ExitCodes.BadArguments;
    }

    /// <summary>Runs the service until the service manager stops it. Blocks: the service manager owns the process.</summary>
    public static void RunService(AgentPaths paths) => ServiceBase.Run(new AgentService(paths));
}

/// <summary>
/// The Windows service: a supervisor only. It owns no network connection and shows nothing; it keeps one agent
/// running in whichever session is on the console (<see cref="AgentSupervisor"/>) and follows the session-change
/// notifications Windows sends it. The agent it launches is today's agent, unchanged.
/// </summary>
internal sealed class AgentService : ServiceBase
{
    private readonly AgentPaths _paths;
    private readonly AgentLog _log;

    private ServiceSessions? _sessions;
    private CancellationTokenSource? _stop;
    private Task? _run;

    public AgentService(AgentPaths paths)
    {
        _paths = paths;
        _log = new AgentLog(paths.LogFile);
        ServiceName = "ProminAgent"; // matches ServiceInstall.ServiceId
        CanHandleSessionChangeEvent = true;
        CanShutdown = true;
    }

    protected override void OnStart(string[] args)
    {
        AgentHost.LogWhatNobodyCatches(_log);
        _sessions = new ServiceSessions(WindowsSessionLauncher.ActiveConsoleSessionOrNull);
        var launcher = new WindowsSessionLauncher(ExecutablePath(), _log.Write);
        var supervisor = new AgentSupervisor(launcher, _sessions, new RestartBackoff(), TimeProvider.System, _log.Write);
        _stop = new CancellationTokenSource();
        _run = Task.Run(() => supervisor.RunAsync(_stop.Token));
        _log.Write($"Promin Agent service started (process {Environment.ProcessId}).");
    }

    protected override void OnStop()
    {
        _stop?.Cancel();
        try
        {
            _run?.Wait(TimeSpan.FromSeconds(15));
        }
        catch (Exception e)
        {
            _log.Write($"The supervisor did not stop cleanly: {e.Message}");
        }

        _log.Write("Promin Agent service stopped.");
    }

    protected override void OnShutdown() => OnStop();

    // Windows tells the service which session signed in, signed out, or came to / left the console; the supervisor
    // follows ActiveSession. Lock and unlock are not a change of which session is on the console, so they are ignored.
    protected override void OnSessionChange(SessionChangeDescription change)
    {
        var @event = change.Reason switch
        {
            SessionChangeReason.SessionLogon or SessionChangeReason.ConsoleConnect => ServiceSessionEvent.SignedInOrConnected,
            SessionChangeReason.SessionLogoff or SessionChangeReason.ConsoleDisconnect => ServiceSessionEvent.SignedOutOrDisconnected,
            _ => (ServiceSessionEvent?)null,
        };

        if (@event is { } e)
        {
            _sessions?.Apply(new ServiceSessionChange(e, change.SessionId));
        }
    }

    private static string ExecutablePath() => System.Diagnostics.Process.GetCurrentProcess().MainModule!.FileName;
}
