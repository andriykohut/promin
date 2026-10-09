using System.Globalization;
using Educast.Master.Core.Wall;
using Educast.Protocol.Video;

namespace Educast.Master.Core.Tests.Wall;

public class WallTextTests
{
    [Theory]
    [InlineData(PcTileState.Ready, 0, "Ready")]
    [InlineData(PcTileState.Offline, 0, "Offline")]
    [InlineData(PcTileState.Receiving, 5, "Receiving")]
    [InlineData(PcTileState.Reconnecting, 5, "Reconnecting")]
    [InlineData(PcTileState.DroppingFrames, 37, "37 frames dropped")]
    [InlineData(PcTileState.DroppingFrames, 1, "1 frame dropped")]
    public void Status(PcTileState state, int dropped, string expected) =>
        Assert.Equal(expected, WallText.Status(state, dropped));

    [Theory]
    [InlineData(9, 1, new[] { "9 ready", "1 offline" })]
    [InlineData(10, 0, new[] { "10 ready" })]
    [InlineData(0, 3, new[] { "3 offline" })]
    public void PillsOffAir(int ready, int offline, string[] expected) =>
        Assert.Equal(expected, WallText.PillsOffAir(ready, offline).Select(p => p.Text));

    [Fact]
    public void PillsOffAir_CarryTheStateTheirDotIsColouredBy() =>
        Assert.Equal(
            [PcTileState.Ready, PcTileState.Offline],
            WallText.PillsOffAir(9, 1).Select(p => p.State));

    [Theory]
    [InlineData(8, 1, 1, new[] { "8 receiving", "1 dropping frames", "1 reconnecting" })]
    [InlineData(9, 0, 0, new[] { "9 receiving" })]
    [InlineData(0, 0, 2, new[] { "2 reconnecting" })]
    public void PillsOnAir(int receiving, int dropping, int reconnecting, string[] expected) =>
        Assert.Equal(expected, WallText.PillsOnAir(receiving, dropping, reconnecting).Select(p => p.Text));

    [Fact]
    public void PillsOnAir_CarryTheStateTheirDotIsColouredBy() =>
        Assert.Equal(
            [PcTileState.Receiving, PcTileState.DroppingFrames, PcTileState.Reconnecting],
            WallText.PillsOnAir(8, 1, 1).Select(p => p.State));

    [Theory]
    [InlineData(9, 10, "9 of 10 PCs selected")]
    [InlineData(1, 1, "1 of 1 PCs selected")]
    public void SelectedLine(int selected, int all, string expected) =>
        Assert.Equal(expected, WallText.SelectedLine(selected, all));

    [Theory]
    [InlineData(new string[0], "")]
    [InlineData(new[] { "PC-07" }, "PC-07 is offline. It joins by itself if it comes back.")]
    [InlineData(new[] { "PC-03", "PC-07" }, "2 PCs are offline. They join by themselves if they come back.")]
    public void OfflineLine(string[] names, string expected) =>
        Assert.Equal(expected, WallText.OfflineLine(names));

    [Theory]
    [InlineData(9, new string[0], new string[0], "All 9 PCs receiving well.")]
    [InlineData(8, new[] { "PC-04" }, new[] { "PC-07" }, "8 PCs receiving well. PC-04 is dropping frames. PC-07 is reconnecting.")]
    [InlineData(1, new[] { "PC-02", "PC-04" }, new string[0], "1 PC receiving well. 2 PCs are dropping frames.")]
    [InlineData(0, new string[0], new[] { "PC-07" }, "0 PCs receiving well. PC-07 is reconnecting.")]
    [InlineData(7, new string[0], new[] { "PC-01", "PC-07" }, "7 PCs receiving well. 2 PCs are reconnecting.")]
    public void OnAirLine(int receiving, string[] dropping, string[] reconnecting, string expected) =>
        Assert.Equal(expected, WallText.OnAirLine(receiving, dropping, reconnecting));

    [Theory]
    [InlineData(12 * 60 + 41, "12:41")]
    [InlineData(5, "0:05")]
    [InlineData(3600 + 2 * 60 + 41, "1:02:41")]
    public void Elapsed(int seconds, string expected) =>
        Assert.Equal(expected, WallText.Elapsed(TimeSpan.FromSeconds(seconds)));

    [Theory]
    [InlineData(PcTileState.DroppingFrames, "Its connection is slow. The other PCs are not affected.")]
    [InlineData(PcTileState.Reconnecting, "It dropped off the network. It rejoins by itself when it is back.")]
    [InlineData(PcTileState.Offline, "It is switched off or not on this network.")]
    [InlineData(PcTileState.Ready, "")]
    [InlineData(PcTileState.Receiving, "")]
    public void DetailSentence(PcTileState state, string expected) =>
        Assert.Equal(expected, WallText.DetailSentence(state));

    [Theory]
    [InlineData(Sharpness.Light, 3_110_400, "Light, 3.1 Mbit/s")]
    [InlineData(Sharpness.Standard, 6_220_800, "Standard, 6.2 Mbit/s")]
    [InlineData(Sharpness.Sharp, 9_331_200, "Sharp, 9.3 Mbit/s")]
    public void SharpnessText(Sharpness sharpness, int bitsPerSecond, string expected) =>
        Assert.Equal(expected, WallText.SharpnessText(sharpness, bitsPerSecond, CultureInfo.InvariantCulture));

    [Fact]
    public void QualityButton_NamesQualityAndRate() =>
        Assert.Equal("1080p, 15 fps, 6.2 Mbit/s", WallText.QualityButton("1080p, 15 fps", 6_220_800, CultureInfo.InvariantCulture));

    [Theory]
    [InlineData(9, "6.2 Mbit/s to each PC, about 56 Mbit/s for 9 PCs.")]
    [InlineData(1, "6.2 Mbit/s to 1 PC.")]
    [InlineData(0, "")]
    public void NetworkLoad(int pcs, string expected) =>
        Assert.Equal(expected, WallText.NetworkLoad(6_220_800, pcs, CultureInfo.InvariantCulture));

    [Fact]
    public void TheBarsFixedLines_AreTheTables()
    {
        Assert.Equal("No PCs to broadcast to", WallText.EmptyRoomLine1);
        Assert.Equal("Set up devices to add the first one.", WallText.EmptyRoomLine2);
        Assert.Equal("On air", WallText.OnAirLine1);
    }

    [Fact]
    public void Pills_CarryTheirCountAndWord()
    {
        var pill = Assert.Single(WallText.PillsOffAir(0, 3));

        Assert.Equal(3, pill.Count);
        Assert.Equal("offline", pill.Word);
        Assert.Equal("3 offline", pill.Text);
    }

    [Theory]
    [InlineData("PC-04", "Select PC-04", "Details for PC-04")]
    [InlineData("Front left", "Select Front left", "Details for Front left")]
    public void AccessibleNames_NameThePc(string name, string selectBox, string details)
    {
        Assert.Equal(selectBox, WallText.SelectBoxName(name));
        Assert.Equal(details, WallText.DetailsButtonName(name));
    }

    [Fact]
    public void AccessibleNames_NameTheStateAndTheChoice()
    {
        Assert.Equal("PC-04, 37 frames dropped", WallText.TileName("PC-04", "37 frames dropped"));
        Assert.Equal("Quality, 1080p, 15 fps, 6.2 Mbit/s", WallText.QualityPickerName("1080p, 15 fps, 6.2 Mbit/s"));
    }

    [Fact]
    public void Rates_FollowTheCulture() =>
        Assert.Equal("Standard, 6,2 Mbit/s", WallText.SharpnessText(Sharpness.Standard, 6_220_800, CultureInfo.GetCultureInfo("de-DE")));
}
