using Promin.Agent;

namespace Promin.Agent.Tests;

public class ServiceInstallTests
{
    private const string AgentExe = "C:\\p\\Promin.Agent.exe";

    private static ServiceInstall.Environment Fake(bool elevated, List<ExternalCommand> ran, Func<ExternalCommand, int>? result = null) =>
        new(() => elevated, cmd => { ran.Add(cmd); return result?.Invoke(cmd) ?? 0; });

    [Fact]
    public void Install_WithoutAdministrator_SaysSo_RunsNothing_AndReturnsNotElevated()
    {
        using var temp = new TempDirectory();
        var ran = new List<ExternalCommand>();
        var output = new StringWriter();

        var code = ServiceInstall.Run(install: true, Fake(elevated: false, ran), temp.Paths, AgentExe, output);

        Assert.Equal(ExitCodes.NotElevated, code);
        Assert.Empty(ran);
        Assert.Contains("administrator", output.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Install_RefusesWhenNotRunFromTheAgentExe()
    {
        using var temp = new TempDirectory();
        var ran = new List<ExternalCommand>();
        var output = new StringWriter();

        // Elevated, but the running exe is dotnet.exe (as under `dotnet run`): must refuse before touching anything.
        var code = ServiceInstall.Run(install: true, Fake(elevated: true, ran), temp.Paths, "C:\\p\\dotnet.exe", output);

        Assert.Equal(ExitCodes.BadArguments, code);
        Assert.Empty(ran);
        Assert.Contains("Promin.Agent.exe", output.ToString());
    }

    [Fact]
    public void Install_RegistersTheService_AddsTheFirewallRule_AndLocksTheFolder()
    {
        using var temp = new TempDirectory();
        var ran = new List<ExternalCommand>();

        var code = ServiceInstall.Run(install: true, Fake(elevated: true, ran), temp.Paths, AgentExe, new StringWriter());

        Assert.Equal(ExitCodes.Success, code);
        Assert.Contains(ran, c => c.FileName == "sc.exe" && c.Arguments.Contains("create") && c.Arguments.Contains("ProminAgent"));
        Assert.Contains(ran, c => c.FileName == "sc.exe" && c.Arguments.Contains("--service"));
        Assert.Contains(ran, c => c.FileName == "netsh.exe" && c.Arguments.Contains("17843"));
        Assert.Contains(ran, c => c.FileName == "icacls.exe");
    }

    [Fact]
    public void Install_IsIdempotent_UpdatesBinPath_AndTreatsRunningServiceAsSuccess()
    {
        using var temp = new TempDirectory();
        var ran = new List<ExternalCommand>();
        // sc create → 1073 (already exists); sc start → 1056 (already running). Install must still succeed.
        var env = Fake(elevated: true, ran, cmd =>
            cmd.Arguments.Contains("create") ? 1073 :
            cmd.Arguments.StartsWith("start ") ? 1056 : 0);

        var code = ServiceInstall.Run(install: true, env, temp.Paths, AgentExe, new StringWriter());

        Assert.Equal(ExitCodes.Success, code);
        // On the already-exists path it reconfigures the binPath rather than leaving a stale one.
        Assert.Contains(ran, c => c.FileName == "sc.exe" && c.Arguments.StartsWith("config ") && c.Arguments.Contains("ProminAgent"));
    }

    [Fact]
    public void Install_WhenARequiredStepFails_ReturnsServiceControlFailed_AndSkipsLaterSteps()
    {
        using var temp = new TempDirectory();
        var ran = new List<ExternalCommand>();
        // The firewall rule fails; the service must not then be started.
        var env = Fake(elevated: true, ran, cmd =>
            cmd.FileName == "netsh.exe" && cmd.Arguments.Contains("add") ? 1 : 0);

        var code = ServiceInstall.Run(install: true, env, temp.Paths, AgentExe, new StringWriter());

        Assert.Equal(ExitCodes.ServiceControlFailed, code);
        Assert.DoesNotContain(ran, c => c.FileName == "sc.exe" && c.Arguments.StartsWith("start "));
    }

    [Fact]
    public void Uninstall_StopsAndDeletesTheService_AndRemovesTheFirewallRule_LeavingDataFiles()
    {
        using var temp = new TempDirectory();
        var ran = new List<ExternalCommand>();
        var output = new StringWriter();

        var code = ServiceInstall.Run(install: false, Fake(elevated: true, ran), temp.Paths, AgentExe, output);

        Assert.Equal(ExitCodes.Success, code);
        Assert.Contains(ran, c => c.FileName == "sc.exe" && c.Arguments.Contains("delete"));
        Assert.Contains(ran, c => c.FileName == "netsh.exe" && c.Arguments.Contains("delete"));
        Assert.Contains(temp.Paths.AgentDirectory, output.ToString()); // tells the user where the data was left
    }

    [Fact]
    public void Uninstall_WhenServiceDoesNotExist_IsStillSuccess()
    {
        using var temp = new TempDirectory();
        var ran = new List<ExternalCommand>();
        // sc delete → 1060 (service does not exist): uninstall is idempotent.
        var env = Fake(elevated: true, ran, cmd => cmd.Arguments.StartsWith("delete ") ? 1060 : 0);

        var code = ServiceInstall.Run(install: false, env, temp.Paths, AgentExe, new StringWriter());

        Assert.Equal(ExitCodes.Success, code);
    }

    [Fact]
    public void CommandStrings_CarryTheExactSecurityRelevantArguments()
    {
        var register = ServiceInstall.Commands.Register(AgentExe);
        Assert.Equal("sc.exe", register.FileName);
        Assert.Contains("binPath= \"\\\"", register.Arguments);
        Assert.Contains("start= auto", register.Arguments);
        Assert.Contains("obj= LocalSystem", register.Arguments);

        var firewall = ServiceInstall.Commands.AddFirewall(AgentExe);
        Assert.Equal("netsh.exe", firewall.FileName);
        Assert.Contains("dir=in", firewall.Arguments);
        Assert.Contains("protocol=UDP", firewall.Arguments);
        Assert.Contains("localport=17843", firewall.Arguments);
        Assert.Contains("program=", firewall.Arguments);

        var lockFolder = ServiceInstall.Commands.LockFolder("C:\\ProgramData\\Promin\\Agent");
        Assert.Equal("icacls.exe", lockFolder.FileName);
        Assert.Contains("/inheritance:r", lockFolder.Arguments);
        Assert.Contains("/grant:r", lockFolder.Arguments);
        Assert.Contains("/setowner", lockFolder.Arguments);
        Assert.Contains("*S-1-5-18", lockFolder.Arguments);
        Assert.Contains("*S-1-5-32-544", lockFolder.Arguments);
    }
}
