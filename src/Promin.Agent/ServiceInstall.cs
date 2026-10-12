using System.Diagnostics;
using System.Security.Principal;

namespace Promin.Agent;

/// <summary>One external command to run: the program and its already-quoted argument string.</summary>
internal readonly record struct ExternalCommand(string FileName, string Arguments);

/// <summary>
/// Installs and removes the agent service: registers it under SYSTEM to start with Windows, opens its UDP port,
/// and locks its data folder to SYSTEM and administrators. The command strings are built in <see cref="Commands"/>
/// and unit-tested; running them and checking elevation are injected through <see cref="Environment"/> so the
/// orchestration is tested with a fake and run for real from <c>Program</c>.
/// </summary>
internal static class ServiceInstall
{
    public const string ServiceId = "ProminAgent";
    public const string DisplayName = "Promin Agent";
    public const string FirewallRule = "Promin Agent (UDP 17843)";

    internal sealed record Environment(Func<bool> IsElevated, Func<ExternalCommand, int> RunCommand);

    internal static class Commands
    {
        // sc.exe treats a leading/trailing space after '=' as significant; keep the "key= value" spacing exactly.
        public static ExternalCommand Register(string exePath) => new("sc.exe",
            $"create {ServiceId} binPath= \"\\\"{exePath}\\\" --service\" start= auto obj= LocalSystem DisplayName= \"{DisplayName}\"");
        public static ExternalCommand Describe() => new("sc.exe",
            $"description {ServiceId} \"Shows broadcasts from the paired console PC and keeps this PC's screen in the room.\"");
        public static ExternalCommand Start() => new("sc.exe", $"start {ServiceId}");
        public static ExternalCommand Stop() => new("sc.exe", $"stop {ServiceId}");
        public static ExternalCommand Delete() => new("sc.exe", $"delete {ServiceId}");
        public static ExternalCommand AddFirewall(string exePath) => new("netsh.exe",
            $"advfirewall firewall add rule name=\"{FirewallRule}\" dir=in action=allow protocol=UDP localport=17843 program=\"{exePath}\"");
        public static ExternalCommand RemoveFirewall() => new("netsh.exe",
            $"advfirewall firewall delete rule name=\"{FirewallRule}\"");
        public static ExternalCommand LockFolder(string agentDir) => new("icacls.exe",
            $"\"{agentDir}\" /inheritance:r /grant:r \"*S-1-5-18:(OI)(CI)F\" \"*S-1-5-32-544:(OI)(CI)F\"");
    }

    /// <summary>The real environment: elevation from the current Windows identity, commands run as hidden child processes.</summary>
    public static Environment ForThisMachine() => new(
        () => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator),
        RunCommand);

    public static int Run(bool install, Environment env, AgentPaths paths, string exePath, TextWriter output)
    {
        if (!env.IsElevated())
        {
            output.WriteLine($"{(install ? "--install" : "--uninstall")} needs an administrator terminal. Right-click Terminal and choose \"Run as administrator\", then run it again.");
            return ExitCodes.NotElevated;
        }

        return install ? Install(env, paths, exePath, output) : Uninstall(env, paths, output);
    }

    private static int Install(Environment env, AgentPaths paths, string exePath, TextWriter output)
    {
        System.IO.Directory.CreateDirectory(paths.AgentDirectory);
        if (Fails(env, Commands.LockFolder(paths.AgentDirectory), output, "lock the data folder")) return ExitCodes.ServiceControlFailed;

        var create = env.RunCommand(Commands.Register(exePath));
        if (create is not (0 or 1073)) // 1073 = service already exists → idempotent
        {
            output.WriteLine($"The service could not be registered (sc.exe returned {create}).");
            return ExitCodes.ServiceControlFailed;
        }

        env.RunCommand(Commands.Describe());
        env.RunCommand(Commands.RemoveFirewall());               // clear any earlier rule so re-install is clean
        if (Fails(env, Commands.AddFirewall(exePath), output, "add the firewall rule")) return ExitCodes.ServiceControlFailed;
        if (Fails(env, Commands.Start(), output, "start the service")) return ExitCodes.ServiceControlFailed;

        output.WriteLine($"Promin Agent is installed and running. It starts with Windows. Data is in {paths.AgentDirectory}.");
        return ExitCodes.Success;
    }

    private static int Uninstall(Environment env, AgentPaths paths, TextWriter output)
    {
        env.RunCommand(Commands.Stop());        // ignore: it may already be stopped
        env.RunCommand(Commands.RemoveFirewall());
        var delete = env.RunCommand(Commands.Delete());
        if (delete is not (0 or 1060))          // 1060 = service does not exist → already gone
        {
            output.WriteLine($"The service could not be removed (sc.exe returned {delete}).");
            return ExitCodes.ServiceControlFailed;
        }

        output.WriteLine($"Promin Agent is removed. Its pairing and identity are left in {paths.AgentDirectory}; delete that folder to forget this PC.");
        return ExitCodes.Success;
    }

    private static bool Fails(Environment env, ExternalCommand command, TextWriter output, string what)
    {
        var code = env.RunCommand(command);
        if (code == 0)
        {
            return false;
        }

        output.WriteLine($"Could not {what} ({command.FileName} returned {code}).");
        return true;
    }

    // Runs one command as a hidden child process and returns its exit code; -1 if it could not be started.
    private static int RunCommand(ExternalCommand command)
    {
        using var process = Process.Start(new ProcessStartInfo(command.FileName, command.Arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        });
        if (process is null)
        {
            return -1;
        }

        process.WaitForExit();
        return process.ExitCode;
    }
}
