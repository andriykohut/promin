namespace Promin.Agent;

internal enum AgentMode
{
    /// <summary>Run as the agent: listen, announce, pair, show broadcasts.</summary>
    Run,

    /// <summary>Store the room code and exit.</summary>
    SetRoomCode,

    /// <summary>Forget the paired console PC and exit.</summary>
    ResetPairing,

    /// <summary>Register the Windows service and exit.</summary>
    Install,

    /// <summary>Remove the Windows service and exit.</summary>
    Uninstall,

    /// <summary>Run under the service manager: supervise the agent in the signed-in user's session.</summary>
    Service,

    /// <summary>Run as the signed-in person: open the files just received, or show a notification when none opened.</summary>
    Received,

    /// <summary>Exit 0 when this process does not run as SYSTEM, 2 when it does: proves a helper started for a person runs as that person.</summary>
    HelperCheck,

    /// <summary>The arguments mean nothing; say so and exit.</summary>
    Invalid,
}

/// <summary>What the agent was asked to do on its command line.</summary>
/// <param name="Windowed">Run showing the picture in an ordinary resizable window instead of covering the display.</param>
/// <param name="RoomCode">The argument of <c>--set-room-code</c>, as typed.</param>
/// <param name="Problem">For <see cref="AgentMode.Invalid"/>, what is wrong with the arguments.</param>
/// <param name="ReceivedCount">For <see cref="AgentMode.Received"/>, how many items arrived at the top level, files and folders alike.</param>
/// <param name="ProminFolder">For <see cref="AgentMode.Received"/>, the Promin folder they arrived in.</param>
/// <param name="OpenPaths">For <see cref="AgentMode.Received"/>, what to open; empty when nothing should be.</param>
internal sealed record AgentCommandLine(
    AgentMode Mode, bool Windowed = false, string? RoomCode = null, string? Problem = null,
    int ReceivedCount = 0, string? ProminFolder = null, IReadOnlyList<string>? OpenPaths = null)
{
    public const string Usage =
        "Promin.Agent.exe [--windowed] | --set-room-code <6 digits> | --reset-pairing | --install | --uninstall | " +
        "--received <count> <Promin folder> [<path to open>...] | --helper-check";

    public static AgentCommandLine Parse(IReadOnlyList<string> args)
    {
        if (args.Count == 0)
        {
            return new AgentCommandLine(AgentMode.Run);
        }

        if (args.Count == 1 && Is(args[0], "--windowed"))
        {
            return new AgentCommandLine(AgentMode.Run, Windowed: true);
        }

        if (args.Count == 1 && Is(args[0], "--reset-pairing"))
        {
            return new AgentCommandLine(AgentMode.ResetPairing);
        }

        if (args.Count == 1 && Is(args[0], "--install"))
        {
            return new AgentCommandLine(AgentMode.Install);
        }

        if (args.Count == 1 && Is(args[0], "--uninstall"))
        {
            return new AgentCommandLine(AgentMode.Uninstall);
        }

        if (args.Count == 1 && Is(args[0], "--service"))
        {
            return new AgentCommandLine(AgentMode.Service);
        }

        if (args.Count == 1 && Is(args[0], "--helper-check"))
        {
            return new AgentCommandLine(AgentMode.HelperCheck);
        }

        if (Is(args[0], "--received"))
        {
            return args.Count >= 3 && int.TryParse(args[1], out var count) && count >= 1
                ? new AgentCommandLine(AgentMode.Received, ReceivedCount: count, ProminFolder: args[2], OpenPaths: args.Skip(3).ToList())
                : Invalid("--received takes a count of one or more, the Promin folder, then the paths to open.");
        }

        if (Is(args[0], "--set-room-code"))
        {
            return args.Count == 2
                ? new AgentCommandLine(AgentMode.SetRoomCode, RoomCode: args[1])
                : Invalid("--set-room-code takes exactly one argument, the six digits.");
        }

        return Invalid($"Unrecognised arguments: {string.Join(' ', args)}");
    }

    private static bool Is(string argument, string option) =>
        string.Equals(argument, option, StringComparison.OrdinalIgnoreCase);

    private static AgentCommandLine Invalid(string problem) => new(AgentMode.Invalid, Problem: problem);
}
