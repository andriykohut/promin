using Promin.Master.Core.Setup;

namespace Promin.Master.Core.Tests.Setup;

public sealed class SetupRowTests
{
    [Theory]
    [InlineData(SetupRowStatus.NotPaired, "Not paired", null)]
    [InlineData(SetupRowStatus.Waiting, "Waiting", null)]
    [InlineData(SetupRowStatus.Pairing, "Pairing", null)]
    [InlineData(SetupRowStatus.InRoom, "In this room", null)]
    [InlineData(SetupRowStatus.WrongCode, "Wrong room code", "This PC was installed with a different code.")]
    [InlineData(SetupRowStatus.Unreachable, "Cannot reach this PC", "It is announcing itself, but its firewall may be blocking Promin.")]
    [InlineData(SetupRowStatus.CannotPairNow, "Cannot pair right now", "Restart Promin on it and try again.")]
    public void StatusText_IsTheDesignsWording(SetupRowStatus status, string text, string? detail)
    {
        var row = new SetupRow("id", "PC-01", "192.168.1.21", status);

        Assert.Equal(text, row.StatusText);
        Assert.Equal(detail, row.StatusDetail);
    }

    [Theory]
    [InlineData(SetupRowStatus.NotPaired, true, false, false, false, false, true)]
    [InlineData(SetupRowStatus.Waiting, false, false, false, false, false, true)]
    [InlineData(SetupRowStatus.Pairing, false, false, false, false, false, false)]
    [InlineData(SetupRowStatus.InRoom, false, false, false, true, false, false)]
    [InlineData(SetupRowStatus.WrongCode, false, true, false, false, true, false)]
    [InlineData(SetupRowStatus.Unreachable, false, false, true, false, true, false)]
    [InlineData(SetupRowStatus.CannotPairNow, false, false, true, false, true, false)]
    public void EachStatus_ShowsExactlyItsAction(
        SetupRowStatus status, bool pair, bool enterCode, bool tryAgain, bool menu, bool failed, bool neutralDot)
    {
        var row = new SetupRow("id", "PC-01", "192.168.1.21", status);

        Assert.Equal(pair, row.ShowsPair);
        Assert.Equal(enterCode, row.ShowsEnterCode);
        Assert.Equal(tryAgain, row.ShowsTryAgain);
        Assert.Equal(menu, row.ShowsMenu);
        Assert.Equal(failed, row.IsFailed);
        Assert.Equal(neutralDot, row.ShowsNeutralDot);
        Assert.Equal(status == SetupRowStatus.InRoom, row.ShowsSuccessDot);
        Assert.Equal(status == SetupRowStatus.Pairing, row.IsPairing);
        Assert.Equal(status is SetupRowStatus.InRoom or SetupRowStatus.Pairing, row.ShowsPlainStatus);
        Assert.True(neutralDot ^ row.ShowsPlainStatus ^ failed);
    }

    [Fact]
    public void ChangingTheStatus_RaisesEveryDerivedProperty()
    {
        var row = new SetupRow("id", "PC-01", "192.168.1.21", SetupRowStatus.NotPaired);
        var raised = new List<string?>();
        row.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        row.Status = SetupRowStatus.WrongCode;

        Assert.Contains(nameof(SetupRow.Status), raised);
        Assert.Contains(nameof(SetupRow.StatusText), raised);
        Assert.Contains(nameof(SetupRow.StatusDetail), raised);
        Assert.Contains(nameof(SetupRow.IsFailed), raised);
        Assert.Contains(nameof(SetupRow.ShowsEnterCode), raised);
        Assert.Contains(nameof(SetupRow.ShowsPair), raised);
    }
}
