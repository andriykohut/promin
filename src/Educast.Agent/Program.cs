namespace Educast.Agent;

internal static class Program
{
    private static int Main(string[] args)
    {
        var command = AgentCommandLine.Parse(args);
        var paths = AgentPaths.ForThisMachine();

        if (command.Mode != AgentMode.Run)
        {
            // Says one line and exits: nothing is started, no window, no listener.
            var output = ConsoleOutput.Open();
            var exitCode = AgentCommands.Execute(command, paths, output);
            output.Flush();
            return exitCode;
        }

        return AgentHost.RunAsync(command, paths).GetAwaiter().GetResult();
    }
}
