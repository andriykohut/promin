using Promin.Master.Core.Setup;

namespace Promin.Master.Core.Tests.Setup;

public sealed class SetupChecklistTests
{
    [Fact]
    public void TheThreeLines_AreTheDesigns_WithThePortFromTheProtocol()
    {
        Assert.Equal(
            [
                new ChecklistItem(1, "Is it switched on", ", and is Promin running on it? It starts with Windows after install."),
                new ChecklistItem(2, "Is it on the same network", " as this PC? Guest Wi-Fi and separate VLANs do not work."),
                new ChecklistItem(3, "Is its firewall letting Promin through?", " Allow UDP port 17843. The setup guide has the steps."),
            ],
            SetupChecklist.Items);
    }

    [Fact]
    public void ThePort_IsTheProtocolsQuicPort()
    {
        Assert.Contains($"UDP port {Promin.Protocol.ProminPorts.Quic}.", SetupChecklist.Items[2].Rest);
    }
}
