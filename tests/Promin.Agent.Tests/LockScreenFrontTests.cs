namespace Promin.Agent.Tests;

public class LockScreenFrontTests
{
    [Fact]
    public void TheFirstLook_IsAlwaysLogged()
    {
        Assert.Contains("at the front", LockScreenWindow.FrontLine(null, inFront: true, frontProcess: 0));
        Assert.Contains("4321", LockScreenWindow.FrontLine(null, inFront: false, frontProcess: 4321));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void NoChange_LogsNothing(bool inFront) =>
        Assert.Null(LockScreenWindow.FrontLine(inFront, inFront, frontProcess: 4321));

    [Fact]
    public void LosingTheFront_NamesWhoHasIt() =>
        Assert.Contains("4321", LockScreenWindow.FrontLine(true, inFront: false, frontProcess: 4321));

    [Fact]
    public void GettingTheFrontBack_IsLogged() =>
        Assert.Contains("at the front", LockScreenWindow.FrontLine(false, inFront: true, frontProcess: 0));
}
