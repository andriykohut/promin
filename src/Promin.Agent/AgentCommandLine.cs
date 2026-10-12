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

    /// <summary>The arguments mean nothing; say so and exit.</summary>
    Invalid,
}

/// <summary>What the agent was asked to do on its command line.</summary>
/// <param name="Windowed">Run showing the picture in an ordinary resizable window instead of covering the display.</param>
/// <param name="RoomCode">The argument of <c>--set-room-code</c>, as typed.</param>
/// <param name="Problem">For <see cref="AgentMode.Invalid"/>, what is wrong with the arguments.</param>
internal sealed record AgentCommandLine(AgentMode Mode, bool Windowed = false, string? RoomCode = null, string? Problem = null)
{
    public const string Usage =
        "Promin.Agent.exe [--windowed] | --set-room-code <6 digits> | --reset-pairing | --install | --uninstall";

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
