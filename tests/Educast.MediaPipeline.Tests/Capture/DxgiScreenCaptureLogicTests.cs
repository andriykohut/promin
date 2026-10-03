using Educast.MediaPipeline.Capture;
using SharpGen.Runtime;
using Vortice.DXGI;

namespace Educast.MediaPipeline.Tests.Capture;

// The parts of DxgiScreenCapture that are decisions rather than GPU calls. These run anywhere.
public class DxgiScreenCaptureLogicTests
{
    [Fact]
    public void Acquire_Success_IsAFrame() =>
        Assert.Equal(DxgiScreenCapture.AcquireOutcome.Frame, DxgiScreenCapture.ClassifyAcquire(Result.Ok));

    [Fact]
    public void Acquire_WaitTimeout_IsATimeout() =>
        Assert.Equal(DxgiScreenCapture.AcquireOutcome.Timeout, DxgiScreenCapture.ClassifyAcquire(ResultCode.WaitTimeout));

    [Fact]
    public void Acquire_AccessLost_IsAccessLost() =>
        Assert.Equal(DxgiScreenCapture.AcquireOutcome.AccessLost, DxgiScreenCapture.ClassifyAcquire(ResultCode.AccessLost));

    [Fact]
    public void Acquire_DeviceRemoved_IsFailedNotRecoverable() =>
        Assert.Equal(DxgiScreenCapture.AcquireOutcome.Failed, DxgiScreenCapture.ClassifyAcquire(ResultCode.DeviceRemoved));

    [Fact]
    public void ClipPointer_FullyInside_KeepsTheWholeShape()
    {
        Assert.True(DxgiScreenCapture.TryClipPointer(100, 50, 32, 32, 1920, 1080, out var r));
        Assert.Equal((100, 50, 32, 32), r);
    }

    [Fact]
    public void ClipPointer_HangingOffTheTopLeft_IsCut()
    {
        Assert.True(DxgiScreenCapture.TryClipPointer(-10, -4, 32, 32, 1920, 1080, out var r));
        Assert.Equal((0, 0, 22, 28), r);
    }

    [Fact]
    public void ClipPointer_HangingOffTheBottomRight_IsCut()
    {
        Assert.True(DxgiScreenCapture.TryClipPointer(1919, 1079, 32, 32, 1920, 1080, out var r));
        Assert.Equal((1919, 1079, 1, 1), r);
    }

    [Theory]
    [InlineData(1920, 0)]
    [InlineData(0, 1080)]
    [InlineData(-32, 0)]
    [InlineData(0, -32)]
    public void ClipPointer_WhollyOffTheDisplay_IsRejected(int x, int y) =>
        Assert.False(DxgiScreenCapture.TryClipPointer(x, y, 32, 32, 1920, 1080, out _));

    [Fact]
    public void ToPointerShape_Monochrome_HalvesTheReportedHeight()
    {
        // DXGI reports a 32x32 monochrome pointer as 32 wide and 64 high (AND mask + XOR mask).
        var buffer = new byte[4 * 64];
        var shape = DxgiScreenCapture.ToPointerShape(type: 1, width: 32, height: 64, pitch: 4, buffer);

        Assert.NotNull(shape);
        Assert.Equal(PointerShapeKind.Monochrome, shape.Kind);
        Assert.Equal(32, shape.Width);
        Assert.Equal(32, shape.Height);
        Assert.Equal(4, shape.Stride);
        Assert.Same(buffer, shape.Pixels);
    }

    [Theory]
    [InlineData(2u, PointerShapeKind.Color)]
    [InlineData(4u, PointerShapeKind.MaskedColor)]
    public void ToPointerShape_ColourKinds_KeepTheReportedSize(uint type, PointerShapeKind kind)
    {
        var shape = DxgiScreenCapture.ToPointerShape(type, width: 32, height: 32, pitch: 128, new byte[128 * 32]);

        Assert.NotNull(shape);
        Assert.Equal(kind, shape.Kind);
        Assert.Equal(32, shape.Height);
        Assert.Equal(128, shape.Stride);
    }

    [Fact]
    public void ToPointerShape_UnknownType_IsNull() =>
        Assert.Null(DxgiScreenCapture.ToPointerShape(type: 3, width: 1, height: 1, pitch: 4, new byte[4]));
}
