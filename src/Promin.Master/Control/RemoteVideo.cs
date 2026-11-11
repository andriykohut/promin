using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml.Controls;
using Promin.Master.Core.Control;
using Promin.MediaPipeline.Decoding;
using Promin.MediaPipeline.Encoding;
using Promin.MediaPipeline.Presenting;
using Promin.Protocol.Messages;
using Promin.Protocol.Video;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Promin.Master.Control;

/// <summary>
/// Decodes the agent PC's screen video from a control session and shows it, letterboxed, on a
/// <see cref="SwapChainPanel"/>. Create it on the UI thread; <see cref="RunAsync"/> decodes and draws on a pool
/// thread, so <see cref="Resize"/> and the drawing share a lock. The caller cancels and awaits
/// <see cref="RunAsync"/> (it ends with <see cref="OperationCanceledException"/> on cancel, or with the session's
/// exception when the session ends) and then disposes on the UI thread, which takes the swap chain off the panel.
/// <see cref="Dispose"/> is idempotent and safe while <see cref="RunAsync"/> is still drawing: later frames and
/// resizes do nothing.
/// </summary>
internal sealed class RemoteVideo : IDisposable
{
    private readonly SwapChainPanel _panel;
    private readonly Action<string> _log;
    private readonly GpuDevice _gpu;
    private readonly MediaFoundationH264Decoder _decoder;
    private readonly PictureBlitter _blitter;
    private readonly IDXGISwapChain1 _swapChain;
    private readonly object _gate = new();
    private PixelSize _size;
    private volatile object? _pictureSize; // a boxed PixelSize, so the UI thread reads it without the draw lock
    private bool _disposed;
    private bool _still;
    private int _refused;

    public RemoteVideo(SwapChainPanel panel, Action<string> log)
    {
        _panel = panel;
        _log = log;
        _gpu = GpuDevice.Create();
        try
        {
            _decoder = new MediaFoundationH264Decoder(_gpu.Device);
            _blitter = new PictureBlitter(_gpu.Device);
            _size = new PixelSize(
                Math.Max(1, (int)(panel.ActualWidth * panel.CompositionScaleX)),
                Math.Max(1, (int)(panel.ActualHeight * panel.CompositionScaleY)));

            using var dxgiDevice = _gpu.Device.QueryInterface<IDXGIDevice>();
            using var adapter = dxgiDevice.GetAdapter();
            using var factory = adapter.GetParent<IDXGIFactory2>();
            _swapChain = factory.CreateSwapChainForComposition(
                _gpu.Device,
                new SwapChainDescription1
                {
                    Width = (uint)_size.Width,
                    Height = (uint)_size.Height,
                    Format = Format.B8G8R8A8_UNorm,
                    SampleDescription = new SampleDescription(1, 0),
                    BufferUsage = Usage.RenderTargetOutput,
                    BufferCount = 2,
                    Scaling = Scaling.Stretch,
                    SwapEffect = SwapEffect.FlipSequential,
                    AlphaMode = AlphaMode.Ignore,
                });
            SwapChainPanelNative.SetSwapChain(panel, _swapChain);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>The size of the newest decoded picture, or null before the first one.</summary>
    public PixelSize? PictureSize => (PixelSize?)_pictureSize;

    /// <summary>Sizes the swap chain to the panel's size times its composition scale. Call from SizeChanged and CompositionScaleChanged.</summary>
    public void Resize(double width, double height, float scaleX, float scaleY)
    {
        var size = new PixelSize((int)(width * scaleX), (int)(height * scaleY));
        if (size.Width <= 0 || size.Height <= 0)
        {
            return;
        }

        lock (_gate)
        {
            if (_disposed || size == _size)
            {
                return;
            }

            try
            {
                _swapChain.ResizeBuffers(0, (uint)size.Width, (uint)size.Height, Format.Unknown, SwapChainFlags.None).CheckError();
                _size = size;
            }
            catch (Exception e)
            {
                // A removed device fails here; the last frame stays on screen rather than the failure reaching the UI thread.
                Trace.TraceWarning("The remote picture could not be resized to {0}x{1}: {2} (HRESULT 0x{3:X8}).",
                    size.Width, size.Height, e.GetType().Name, Marshal.GetHRForException(e));
            }
        }
    }

    /// <summary>While true, new pictures are decoded but not shown: the other PC is on a protected screen.</summary>
    public void ShowStill(bool still)
    {
        lock (_gate)
        {
            _still = still;
        }
    }

    /// <summary>Receives, decodes and shows frames until <paramref name="ct"/> is cancelled or the session ends (which throws).</summary>
    public Task RunAsync(IControlSession session, CancellationToken ct) => Task.Run(async () =>
    {
        var gate = new StaleFrameGate();
        var keyframeAsk = new KeyframeAsk();
        while (true)
        {
            var payload = await session.ReceiveFrameAsync(ct);
            if (!gate.TryAccept(payload, out var envelope, out var encoded))
            {
                continue;
            }

            if (keyframeAsk.ShouldAsk(envelope.FrameId, _decoder.NeedsKeyframe, DateTimeOffset.UtcNow))
            {
                await session.SendAsync(new RequestKeyframe(), ct);
            }

            if (!_decoder.TryDecode(new EncodedFrame(encoded.ToArray(), envelope.IsKeyframe, envelope.FrameId), out var picture))
            {
                // A decoder that is refused often would fill the log; the first few say why, then now and then.
                _refused++;
                if (_refused <= 5 || _refused % 100 == 0)
                {
                    _log($"The decoder gave no picture for frame {envelope.FrameId}: {_decoder.LastFailure ?? "none came back"}. Refused so far: {_refused}.");
                }

                continue;
            }

            Show(picture);
        }
    }, ct);

    private void Show(DecodedFrame picture)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _pictureSize = new PixelSize(picture.Width, picture.Height);
            if (_still)
            {
                return;
            }

            using (var backBuffer = _swapChain.GetBuffer<ID3D11Texture2D>(0))
            {
                _blitter.Draw(picture.Texture, picture.Width, picture.Height, backBuffer, _size);
            }

            _swapChain.Present(1, PresentFlags.None).CheckError();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        if (_swapChain is not null)
        {
            SwapChainPanelNative.SetSwapChain(_panel, null);
        }

        _swapChain?.Dispose();
        _blitter?.Dispose();
        _decoder?.Dispose();
        _gpu.Dispose();
    }
}
