using Promin.Protocol.Pairing;

namespace Promin.Agent;

/// <summary>
/// The command-line forms that do one thing and exit: they start nothing (no listener, no window). Each prints
/// one line and returns the exit code.
/// </summary>
internal static class AgentCommands
{
    public static int Execute(AgentCommandLine command, AgentPaths paths, TextWriter output, ServiceInstall.Environment? serviceEnvironment = null) => command.Mode switch
    {
        AgentMode.SetRoomCode => SetRoomCode(command.RoomCode, paths, output),
        AgentMode.ResetPairing => ResetPairing(paths, output),
        AgentMode.Install => ServiceInstall.Run(install: true, serviceEnvironment ?? ServiceInstall.ForThisMachine(), paths, ExePath(), output),
        AgentMode.Uninstall => ServiceInstall.Run(install: false, serviceEnvironment ?? ServiceInstall.ForThisMachine(), paths, ExePath(), output),
        AgentMode.Invalid => Refuse(command.Problem, output),
        _ => throw new ArgumentException($"{command.Mode} is not a command that exits.", nameof(command)),
    };

    private static string ExePath() => Environment.ProcessPath!;

    private static int SetRoomCode(string? argument, AgentPaths paths, TextWriter output)
    {
        if (!RoomCode.TryNormalize(argument, out var code))
        {
            output.WriteLine("The room code must be exactly 6 digits (spaces or hyphens between them are fine).");
            return ExitCodes.BadArguments;
        }

        try
        {
            new AgentSettings(paths.RoomCodeFile).Save(code);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            output.WriteLine($"The room code could not be saved to {paths.RoomCodeFile}: {e.Message}");
            return ExitCodes.FileUnusable;
        }

        output.WriteLine($"Room code saved to {paths.RoomCodeFile}.");
        return ExitCodes.Success;
    }

    private static int ResetPairing(AgentPaths paths, TextWriter output)
    {
        if (!File.Exists(paths.MastersFile))
        {
            output.WriteLine($"No console PC is paired under this Windows account ({paths.MastersFile} does not exist).");
            return ExitCodes.Success;
        }

        try
        {
            File.Delete(paths.MastersFile);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            output.WriteLine($"The paired console PC could not be forgotten ({paths.MastersFile}): {e.Message}");
            return ExitCodes.FileUnusable;
        }

        output.WriteLine(
            $"Forgot the paired console PC (deleted {paths.MastersFile}). An agent that is already running " +
            "keeps it until it is restarted.");
        return ExitCodes.Success;
    }

    private static int Refuse(string? problem, TextWriter output)
    {
        output.WriteLine(problem);
        output.WriteLine($"Usage: {AgentCommandLine.Usage}");
        return ExitCodes.BadArguments;
    }
}
