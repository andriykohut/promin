namespace Promin.Master.Core.Tests;

public sealed class ConsoleHealthTests
{
    [Fact]
    public void TheTwoFaults_CarryTheControllersWording()
    {
        Assert.Equal("This PC's certificate cannot be used.", LocalFault.UnusableCertificate.Title);
        Assert.Equal(
            "Promin cannot pair or broadcast until it is repaired. Restart Promin; if this stays, remove the " +
            @"folder %LocalAppData%\Promin\identity and pair the room again.",
            LocalFault.UnusableCertificate.Message);
        Assert.Equal("Promin needs Windows 11.", LocalFault.NoSecureTransport.Title);
        Assert.Equal(
            "This version of Windows cannot make the secure connections Promin uses.",
            LocalFault.NoSecureTransport.Message);
    }

    [Fact]
    public void SettingAFault_RaisesBothProperties()
    {
        var health = new ConsoleHealth();
        var raised = new List<string?>();
        health.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        health.LocalFault = LocalFault.NoSecureTransport;

        Assert.True(health.HasLocalFault);
        Assert.Contains(nameof(ConsoleHealth.LocalFault), raised);
        Assert.Contains(nameof(ConsoleHealth.HasLocalFault), raised);
    }
}
