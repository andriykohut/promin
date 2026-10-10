using Promin.MediaPipeline.Decoding;
using Vortice.Direct3D11;

namespace Promin.MediaPipeline.Tests.Decoding;

/// <summary>
/// The decoder's texture is drawn from by the video processor, which refuses an input texture whose bind flags are
/// neither 0 nor include one of decoder, video encoder, render target or unordered access. A texture bound for
/// shader resource alone was refused on a real GPU, so it is never offered alone.
/// </summary>
public class OutputSurfaceBindFlagsTests
{
    private static bool IsAllowedAsVideoProcessorInput(BindFlags flags) =>
        flags == BindFlags.None
        || (flags & (BindFlags.Decoder | BindFlags.VideoEncoder | BindFlags.RenderTarget | BindFlags.UnorderedAccess)) != 0;

    [Theory]
    [InlineData(FormatSupport.RenderTarget | FormatSupport.ShaderLoad, BindFlags.RenderTarget | BindFlags.ShaderResource)]
    [InlineData(FormatSupport.RenderTarget, BindFlags.RenderTarget)]
    public void WhereTheDeviceCanRenderToNv12_TheTextureIsBoundForRenderTarget_AndThenBareAsAFallback(
        FormatSupport support, BindFlags first)
    {
        var choices = OutputSurface.ChooseBindFlags(support);

        Assert.Equal([first, BindFlags.None], choices);
    }

    [Theory]
    [InlineData(FormatSupport.None)]
    [InlineData(FormatSupport.ShaderLoad)]
    [InlineData(FormatSupport.Texture2D | FormatSupport.ShaderLoad)]
    public void WhereItCannot_TheTextureHasNoBindFlags_EvenIfShadersCanLoadIt(FormatSupport support)
    {
        Assert.Equal([BindFlags.None], OutputSurface.ChooseBindFlags(support));
    }

    [Theory]
    [InlineData(FormatSupport.None)]
    [InlineData(FormatSupport.ShaderLoad)]
    [InlineData(FormatSupport.RenderTarget)]
    [InlineData(FormatSupport.RenderTarget | FormatSupport.ShaderLoad)]
    public void NoChoiceIsEverOneTheVideoProcessorRefusesAsInput(FormatSupport support)
    {
        Assert.All(OutputSurface.ChooseBindFlags(support), flags => Assert.True(IsAllowedAsVideoProcessorInput(flags), $"{flags}"));
    }
}
