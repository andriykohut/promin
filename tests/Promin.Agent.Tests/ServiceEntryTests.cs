using Promin.Agent;

namespace Promin.Agent.Tests;

public class ServiceEntryTests
{
    [Fact]
    public void RefusesWhenRunInteractively_WithOneLine_AndBadArguments()
    {
        var output = new StringWriter();
        var code = ServiceEntry.RefuseIfInteractive(interactive: true, output);
        Assert.Equal(2, code); // ExitCodes.BadArguments
        Assert.Single(output.ToString().TrimEnd().Split('\n'));
        Assert.Contains("service", output.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AllowsWhenRunByTheServiceManager()
    {
        var code = ServiceEntry.RefuseIfInteractive(interactive: false, TextWriter.Null);
        Assert.Equal(-1, code); // -1 means "not refused; carry on"
    }
}

public class ServiceSessionsTests
{
    [Fact]
    public void SignInMakesThatSessionActive_SignOutClearsIt()
    {
        var sessions = new ServiceSessions(() => null); // nothing on the console at start
        Assert.Null(sessions.ActiveSession);

        sessions.Apply(new ServiceSessionChange(ServiceSessionEvent.SignedInOrConnected, 3));
        Assert.Equal(3u, sessions.ActiveSession);

        sessions.Apply(new ServiceSessionChange(ServiceSessionEvent.SignedOutOrDisconnected, 3));
        Assert.Null(sessions.ActiveSession);
    }

    [Fact]
    public void ASignOutForADifferentSessionIsIgnored()
    {
        var sessions = new ServiceSessions(() => null);
        sessions.Apply(new ServiceSessionChange(ServiceSessionEvent.SignedInOrConnected, 3));

        sessions.Apply(new ServiceSessionChange(ServiceSessionEvent.SignedOutOrDisconnected, 9));

        Assert.Equal(3u, sessions.ActiveSession);
    }
}
