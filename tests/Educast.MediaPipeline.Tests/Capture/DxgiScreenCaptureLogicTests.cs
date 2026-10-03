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
        var shape = DxgiScreenCapture.ToPointerShape(PointerShapeType.Monochrome, width: 32, height: 64, pitch: 4, buffer);

        Assert.NotNull(shape);
        Assert.Equal(PointerShapeKind.Monochrome, shape.Kind);
        Assert.Equal(32, shape.Width);
        Assert.Equal(32, shape.Height);
        Assert.Equal(4, shape.Stride);
        Assert.Same(buffer, shape.Pixels);
    }

    [Theory]
    [InlineData(PointerShapeType.Color, PointerShapeKind.Color)]
    [InlineData(PointerShapeType.MaskedColor, PointerShapeKind.MaskedColor)]
    public void ToPointerShape_ColourKinds_KeepTheReportedSize(PointerShapeType type, PointerShapeKind kind)
    {
        var shape = DxgiScreenCapture.ToPointerShape(type, width: 32, height: 32, pitch: 128, new byte[128 * 32]);

        Assert.NotNull(shape);
        Assert.Equal(kind, shape.Kind);
        Assert.Equal(32, shape.Height);
        Assert.Equal(128, shape.Stride);
    }

    [Fact]
    public void ToPointerShape_UnknownType_IsNull() =>
        Assert.Null(DxgiScreenCapture.ToPointerShape((PointerShapeType)3, width: 1, height: 1, pitch: 4, new byte[4]));

    // The numbers Desktop Duplication reports (DXGI_OUTDUPL_POINTER_SHAPE_TYPE) are what the
    // capture casts the raw field to; pin them so a binding change cannot silently remap shapes.
    [Fact]
    public void PointerShapeType_HasTheValuesDxgiReports()
    {
        Assert.Equal(1, (int)PointerShapeType.Monochrome);
        Assert.Equal(2, (int)PointerShapeType.Color);
        Assert.Equal(4, (int)PointerShapeType.MaskedColor);
    }

    // DecideFrame(lastPresentTime, lastMouseUpdateTime, haveDesktop)
    [Theory]
    [InlineData(0L, 0L, false)]
    [InlineData(0L, 0L, true)]
    public void Decide_NothingChanged_IsNoFrame(long present, long mouse, bool haveDesktop) =>
        Assert.Equal(DxgiScreenCapture.FrameDecision.NoFrame, DxgiScreenCapture.DecideFrame(present, mouse, haveDesktop));

    [Theory]
    [InlineData(5L, 0L, false)]
    [InlineData(5L, 0L, true)]
    [InlineData(5L, 7L, false)]
    [InlineData(5L, 7L, true)]
    public void Decide_ScreenChanged_CopiesTheAcquiredSurface(long present, long mouse, bool haveDesktop) =>
        Assert.Equal(DxgiScreenCapture.FrameDecision.CopyAcquiredThenFrame, DxgiScreenCapture.DecideFrame(present, mouse, haveDesktop));

    [Fact]
    public void Decide_PointerOnly_WithAnImage_RedrawsTheExistingImage() =>
        Assert.Equal(DxgiScreenCapture.FrameDecision.FromExistingImage, DxgiScreenCapture.DecideFrame(0, 7, haveDesktop: true));

    // Regression: a pointer-only update on a fresh duplication (or after a rebuild) used to wait for a
    // screen change. On a still slide none comes, so no frame was ever produced.
    [Fact]
    public void Decide_PointerOnly_WithNoImageYet_CopiesTheAcquiredSurfaceInsteadOfWaiting() =>
        Assert.Equal(DxgiScreenCapture.FrameDecision.CopyAcquiredThenFrame, DxgiScreenCapture.DecideFrame(0, 7, haveDesktop: false));

    [Fact]
    public void RetryPause_InfiniteTimeout_IsTheCapNotForever() =>
        Assert.Equal(TimeSpan.FromMilliseconds(100), DxgiScreenCapture.RetryPauseFor(Timeout.InfiniteTimeSpan));

    [Theory]
    [InlineData(0, 0)]
    [InlineData(30, 30)]
    [InlineData(100, 100)]
    [InlineData(101, 100)]
    [InlineData(5000, 100)]
    public void RetryPause_FiniteTimeout_IsCappedAtTheRetryPause(int timeoutMs, int expectedMs) =>
        Assert.Equal(TimeSpan.FromMilliseconds(expectedMs), DxgiScreenCapture.RetryPauseFor(TimeSpan.FromMilliseconds(timeoutMs)));
    [Fact]
    public void SurfaceFormat_OnlyBgra8IsAcceptable()
    {
        Assert.True(DxgiScreenCapture.IsAcceptableSurfaceFormat(Format.B8G8R8A8_UNorm));
        Assert.False(DxgiScreenCapture.IsAcceptableSurfaceFormat(Format.R16G16B16A16_Float)); // what colour management hands out
        Assert.False(DxgiScreenCapture.IsAcceptableSurfaceFormat(Format.R8G8B8A8_UNorm));
        Assert.False(DxgiScreenCapture.IsAcceptableSurfaceFormat(Format.B8G8R8A8_UNorm_SRgb));
        Assert.False(DxgiScreenCapture.IsAcceptableSurfaceFormat(Format.R10G10B10A2_UNorm));
    }

    [Fact]
    public void DuplicateOutput1_IsOnlyAskedForBgra8() =>
        Assert.Equal([Format.B8G8R8A8_UNorm], DxgiScreenCapture.SupportedFormats);

    [Fact]
    public void UnsupportedSurface_Message_NamesTheFormat() =>
        Assert.Contains("R16G16B16A16_Float",
            DxgiScreenCapture.DescribeUnsupportedSurface(Format.R16G16B16A16_Float, true, null));

    [Fact]
    public void UnsupportedSurface_NotDpiAware_SaysToDeclarePerMonitorV2()
    {
        var message = DxgiScreenCapture.DescribeUnsupportedSurface(Format.R16G16B16A16_Float, false, null);

        Assert.Contains("PerMonitorV2", message);
    }

    [Fact]
    public void UnsupportedSurface_DpiAware_DoesNotBlameDpi() =>
        Assert.DoesNotContain("DPI", DxgiScreenCapture.DescribeUnsupportedSurface(Format.R16G16B16A16_Float, true, null));

    [Fact]
    public void UnsupportedSurface_ReportsWhyDuplicateOutput1Failed()
    {
        var message = DxgiScreenCapture.DescribeUnsupportedSurface(Format.R16G16B16A16_Float, true, ResultCode.AccessDenied);

        Assert.Contains("DuplicateOutput1", message);
        Assert.Contains(ResultCode.AccessDenied.Code.ToString("X8"), message);
    }}
