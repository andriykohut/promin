using static Promin.Agent.NativeMethods;

namespace Promin.Agent;

/// <summary>
/// Where the one-line answers of the command-line forms go. The agent is a windowless program, so it has no
/// console of its own: when its output is redirected that is where the line goes, and otherwise it writes to the
/// console of the program that started it.
/// </summary>
internal static class ConsoleOutput
{
    public static TextWriter Open()
    {
        var standardOutput = GetStdHandle(STD_OUTPUT_HANDLE);
        if (standardOutput != 0 && standardOutput != -1)
        {
            return Console.Out;
        }

        if (!AttachConsole(ATTACH_PARENT_PROCESS))
        {
            return TextWriter.Null; // started from Explorer or a service: there is nobody to tell
        }

        try
        {
            return new StreamWriter(new FileStream("CONOUT$", FileMode.Open, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return TextWriter.Null;
        }
    }
}
