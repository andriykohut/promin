namespace Promin.Agent;

internal static class Program
{
    private static int Main(string[] args)
    {
        var command = AgentCommandLine.Parse(args);
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
}
