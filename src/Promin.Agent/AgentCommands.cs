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
        var said = new List<string>();
        var exitCode = ExitCodes.Success;
        var deleted = false;

        if (!TryDelete(paths.MastersFile, out var found, out var problem))
        {
            said.Add($"The paired console PC could not be forgotten ({paths.MastersFile}): {problem}");
            exitCode = ExitCodes.FileUnusable;
        }
        else if (found)
        {
            said.Add($"Forgot the paired console PC (deleted {paths.MastersFile}).");
            deleted = true;
        }
        else
        {
            said.Add($"No console PC is paired ({paths.MastersFile} does not exist).");
        }

        // A lock is cleared even when nothing is paired: this is the way out of a lock in Safe Mode.
        if (!TryDelete(paths.LockFile, out found, out problem))
        {
            said.Add($"The lock could not be cleared ({paths.LockFile}): {problem}");
            exitCode = ExitCodes.FileUnusable;
        }
        else if (found)
        {
            said.Add($"Cleared the lock (deleted {paths.LockFile}).");
            deleted = true;
        }

        if (deleted)
        {
            said.Add("An agent that is already running keeps them until it is restarted.");
        }

        output.WriteLine(string.Join(' ', said));
        return exitCode;
    }

    private static bool TryDelete(string path, out bool found, out string? problem)
    {
        found = false;
        problem = null;
        try
        {
            // File.Exists says false for a file this account may not look at, and File.Delete says nothing about a
            // missing one. This throws in both cases, with a different exception for each.
            File.GetAttributes(path);
            found = true;
            File.Delete(path);
            return true;
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            problem = e.Message;
            return false;
        }
    }

    private static int Refuse(string? problem, TextWriter output)
    {
        output.WriteLine(problem);
        output.WriteLine($"Usage: {AgentCommandLine.Usage}");
        return ExitCodes.BadArguments;
    }
}
