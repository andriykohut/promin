using Promin.Agent;

namespace Promin.Agent.Tests;

public class ServiceInstallTests
{
    private static ServiceInstall.Environment Fake(bool elevated, List<ExternalCommand> ran, Func<ExternalCommand, int>? result = null) =>
        new(() => elevated, cmd => { ran.Add(cmd); return result?.Invoke(cmd) ?? 0; });

    [Fact]
    public void Install_WithoutAdministrator_SaysSo_RunsNothing_AndReturnsNotElevated()
    {
        using var temp = new TempDirectory();
        var ran = new List<ExternalCommand>();
        var output = new StringWriter();

        var code = ServiceInstall.Run(install: true, Fake(elevated: false, ran), temp.Paths, "C:\\p\\Promin.Agent.exe", output);

        Assert.Equal(ExitCodes.NotElevated, code);
        Assert.Empty(ran);
        Assert.Contains("administrator", output.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Install_RegistersTheService_AddsTheFirewallRule_AndLocksTheFolder()
    {
        using var temp = new TempDirectory();
        var ran = new List<ExternalCommand>();

        var code = ServiceInstall.Run(install: true, Fake(elevated: true, ran), temp.Paths, "C:\\p\\Promin.Agent.exe", new StringWriter());

        Assert.Equal(ExitCodes.Success, code);
        Assert.Contains(ran, c => c.FileName == "sc.exe" && c.Arguments.Contains("create") && c.Arguments.Contains("ProminAgent"));
        Assert.Contains(ran, c => c.FileName == "sc.exe" && c.Arguments.Contains("--service"));
        Assert.Contains(ran, c => c.FileName == "netsh.exe" && c.Arguments.Contains("17843"));
        Assert.Contains(ran, c => c.FileName == "icacls.exe");
    }

    [Fact]
    public void Install_IsIdempotent_TreatingAnAlreadyExistingServiceAsSuccess()
    {
        using var temp = new TempDirectory();
        var ran = new List<ExternalCommand>();
        // sc create returns 1073 (service already exists); install must still succeed.
        var env = Fake(elevated: true, ran, cmd => cmd.Arguments.Contains("create") ? 1073 : 0);

        var code = ServiceInstall.Run(install: true, env, temp.Paths, "C:\\p\\Promin.Agent.exe", new StringWriter());

        Assert.Equal(ExitCodes.Success, code);
    }

    [Fact]
    public void Uninstall_StopsAndDeletesTheService_AndRemovesTheFirewallRule_LeavingDataFiles()
    {
        using var temp = new TempDirectory();
        var ran = new List<ExternalCommand>();
        var output = new StringWriter();

        var code = ServiceInstall.Run(install: false, Fake(elevated: true, ran), temp.Paths, "C:\\p\\Promin.Agent.exe", output);

        Assert.Equal(ExitCodes.Success, code);
        Assert.Contains(ran, c => c.FileName == "sc.exe" && c.Arguments.Contains("delete"));
        Assert.Contains(ran, c => c.FileName == "netsh.exe" && c.Arguments.Contains("delete"));
        Assert.Contains(temp.Paths.AgentDirectory, output.ToString()); // tells the user where the data was left
    }
}
