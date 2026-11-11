using Promin.Master.Core.Control;
using Promin.Protocol.Video;

namespace Promin.Master.Core.Tests.Control;

public class ControlGeometryTests
{
    [Fact]
    public void TheCentreOfAFittedPicture_IsTheCentreOfTheScreen() =>
        Assert.Equal(((ushort)32768, (ushort)32768), ControlGeometry.ToRemote(800, 450, 1600, 900, new PixelSize(1920, 1080)));

    [Fact]
    public void ThePillarbox_IsOutside() =>
        Assert.Null(ControlGeometry.ToRemote(10, 450, 1600, 900, new PixelSize(1024, 768)));

    [Fact]
    public void TheLetterbox_IsOutside() =>
        Assert.Null(ControlGeometry.ToRemote(800, 10, 900, 1600, new PixelSize(1920, 1080)));

    [Fact]
    public void TheFarCorner_Is65535() =>
        Assert.Equal(((ushort)65535, (ushort)65535), ControlGeometry.ToRemote(1599.99, 899.99, 1600, 900, new PixelSize(1920, 1080)));

    [Fact]
    public void ANewPictureSize_RefitsTheMapping()
    {
        Assert.Equal(((ushort)16384, (ushort)32768), ControlGeometry.ToRemote(400, 450, 1600, 900, new PixelSize(1920, 1080)));
        Assert.Equal(((ushort)9466, (ushort)32768), ControlGeometry.ToRemote(400, 450, 1600, 900, new PixelSize(1280, 1024)));
    }
}
