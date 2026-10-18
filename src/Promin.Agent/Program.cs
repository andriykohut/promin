using System.Diagnostics;
using System.Security.Principal;

namespace Promin.Agent;

internal static class Program
{
    /// <summary>What <c>--helper-check</c> exits with when the helper runs as SYSTEM instead of as the signed-in person.</summary>
    internal const int HelperRunsAsSystem = 2;

    private static int Main(string[] args)
    {
        var command = AgentCommandLine.Parse(args);

        if (command.Mode is AgentMode.Received or AgentMode.HelperCheck)
        {
            return RunHelper(command);
        }

        var paths = AgentPaths.ForThisMachine();

        if (command.Mode == AgentMode.Service)
        {
            // The service manager starts this; a person who types it gets one line and nothing else.
            var serviceOutput = ConsoleOutput.Open();
            var refused = ServiceEntry.RefuseIfInteractive(Environment.UserInteractive, serviceOutput);
            serviceOutput.Flush();
            if (refused >= 0)
            {
                return refused;
            }

            ServiceEntry.RunService(paths);
            return ExitCodes.Success;
        }

        if (command.Mode != AgentMode.Run)
        {
            // Says one line and exits: nothing is started, no window, no listener.
            var output = ConsoleOutput.Open();
            var exitCode = AgentCommands.Execute(command, paths, output);
            output.Flush();
            return exitCode;
        }

        var log = new AgentLog(paths.LogFile);
        AgentHost.LogWhatNobodyCatches(log);
        return AgentHost.RunAsync(command, paths, log, AgentServices.ForThisMachine(), CancellationToken.None)
            .GetAwaiter().GetResult();
    }

    /// <summary>
    /// The helper modes run as the signed-in person, who cannot write the agent's log folder, so they print and log
    /// nothing: the exit code is the only report.
    /// </summary>
    private static int RunHelper(AgentCommandLine command)
    {
        try
        {
            if (command.Mode == AgentMode.HelperCheck)
            {
                using var identity = WindowsIdentity.GetCurrent();
                return identity.IsSystem ? HelperRunsAsSystem : ExitCodes.Success;
            }

            ReceivedCommand.Run(
                command.ReceivedCount, command.ProminFolder!, command.OpenPaths!,
                DangerousTypes.IsDangerous, ShellOpen, ReceivedNotice.Show);
            return ExitCodes.Success;
        }
        catch (Exception)
        {
            return ExitCodes.Unexpected;
        }
    }

    /// <summary>Opens a file with its default app, or a folder in Explorer; false when Windows could not.</summary>
    private static bool ShellOpen(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
