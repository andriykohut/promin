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

    private const string AgentExeName = "Promin.Agent.exe";

    internal sealed record Environment(Func<bool> IsElevated, Func<ExternalCommand, int> RunCommand);

    internal static class Commands
    {
        // sc.exe treats a leading/trailing space after '=' as significant; keep the "key= value" spacing exactly.
        public static ExternalCommand Register(string exePath) => new("sc.exe",
            $"create {ServiceId} binPath= \"\\\"{exePath}\\\" --service\" start= auto obj= LocalSystem DisplayName= \"{DisplayName}\"");
        // Run when the service already exists, so a moved or re-published exe updates its binPath instead of keeping a stale one.
        public static ExternalCommand Reconfigure(string exePath) => new("sc.exe",
            $"config {ServiceId} binPath= \"\\\"{exePath}\\\" --service\" start= auto obj= LocalSystem");
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
        // A separate call: icacls rejects /setowner combined with /grant or /inheritance (error 87). Giving the folder to
        // Administrators means a standard user who pre-created it keeps no ownership (and no WRITE_DAC).
        public static ExternalCommand SetOwner(string agentDir) => new("icacls.exe",
            $"\"{agentDir}\" /setowner \"*S-1-5-32-544\"");
    }

    /// <summary>The real environment: elevation from the current Windows identity, commands run as hidden child processes.</summary>
    public static Environment ForThisMachine() => new(
        () => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator),
        RunCommand);

    public static int Run(bool install, Environment env, AgentPaths paths, string exePath, TextWriter output)
    {
        // Under `dotnet run`, exePath is dotnet.exe; registering that as the service would be wrong.
        if (!string.Equals(Path.GetFileName(exePath), AgentExeName, StringComparison.OrdinalIgnoreCase))
        {
            output.WriteLine("Run --install from the published Promin.Agent.exe, not via dotnet run.");
            return ExitCodes.BadArguments;
        }

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

        // Best effort: the lock already keeps standard users out of a folder the installer created; re-owning it also
        // covers a folder a standard user pre-created. A failure here never blocks the install.
        var owned = env.RunCommand(Commands.SetOwner(paths.AgentDirectory));
        if (owned != 0)
        {
            output.WriteLine($"Note: the data folder's owner could not be set (icacls returned {owned}); it is still locked to SYSTEM and administrators.");
        }

        var create = env.RunCommand(Commands.Register(exePath));
        if (create == 1073) // service already exists → update its binPath/start/account in case the exe moved
        {
            env.RunCommand(Commands.Reconfigure(exePath));
        }
        else if (create != 0)
        {
            output.WriteLine($"The service could not be registered (sc.exe returned {create}).");
            return ExitCodes.ServiceControlFailed;
        }

        env.RunCommand(Commands.Describe());
        env.RunCommand(Commands.RemoveFirewall());               // clear any earlier rule so re-install is clean
        if (Fails(env, Commands.AddFirewall(exePath), output, "add the firewall rule")) return ExitCodes.ServiceControlFailed;

        var start = env.RunCommand(Commands.Start());
        if (start is not (0 or 1056)) // 1056 = service already running → fine on a re-install
        {
            output.WriteLine($"The service could not be started (sc.exe returned {start}).");
            return ExitCodes.ServiceControlFailed;
        }

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

    // Runs one command as a hidden child process and returns its exit code. These run elevated during --install, so the
    // Windows tools are resolved to their absolute System32 path: a bare name could run a planted exe from the agent's
    // own folder. A start failure is reported as a non-zero exit (-1) so the orchestration returns ServiceControlFailed.
    private static int RunCommand(ExternalCommand command)
    {
        var fileName = command.FileName is "sc.exe" or "netsh.exe" or "icacls.exe"
            ? Path.Combine(System.Environment.SystemDirectory, command.FileName)
            : command.FileName;

        try
        {
            using var process = Process.Start(new ProcessStartInfo(fileName, command.Arguments)
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
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return -1;
        }
    }
}
