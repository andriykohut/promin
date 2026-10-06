using System.Runtime.InteropServices;
using Educast.Protocol.Video;
using Vortice.Direct3D11;
using Vortice.DXGI;
using static Educast.Agent.NativeMethods;

namespace Educast.Agent;

/// <summary>
/// The student's render window: a minimal Win32 window and a DXGI swap chain, no UI framework. Fullscreen it is
/// borderless and topmost over the primary display in real pixels (the process is per-monitor DPI aware), black
/// where the picture does not reach, with no text and no controls, and the student's own pointer hidden over it.
/// With <c>windowed</c> it is an ordinary resizable window, for running console and agent side by side.
/// </summary>
/// <remarks>
/// <para>
/// <b>Threading.</b> A Win32 window belongs to the thread that creates it. Everything here (the constructor,
/// <see cref="RunMessageLoop"/>, <see cref="PresentFrame"/>, <see cref="Dispose"/>) is called on that one thread.
/// The only calls that may come from any other thread are <see cref="PostAppMessage"/> and
/// <see cref="PostClose"/>, which post a message to the window's queue.
/// </para>
/// <para>
/// <b>Close.</b> Fullscreen, a request to close the window (Alt+F4, the system menu) is ignored: a broadcast ends
/// when the teacher stops it or the connection is lost, not when the student presses a key. Windowed, it closes the
/// window and so ends that broadcast's display; the agent carries on. The window never ends the process.
/// </para>
/// <para>
/// <b>Drawing.</b> The back buffer is the screen's size, not the picture's. <see cref="PresentFrame"/> draws the
/// decoded NV12 picture with the video processor (<see cref="PictureBlitter"/>) straight into the back buffer.
/// There is no <c>CopyResource</c> on this path. The swap chain is flip-model and is never given to DXGI's own
/// fullscreen handling (no Alt+Enter).
/// </para>
/// </remarks>
internal sealed class FullscreenRenderWindow : IDisposable
{
    private const string ClassName = "Educast.Agent.Render";
    private static readonly Lock ClassGate = new();
    private static bool s_classRegistered;

    // The native side holds a function pointer to this, so it must never be collected.
    private static readonly WindowProcedure s_windowProcedure = RouteMessage;

    private readonly ID3D11Device _device;
    private readonly bool _windowed;
    private readonly PictureBlitter _blitter;
    private readonly IDXGISwapChain1 _swapChain;
    private GCHandle _self;
    private nint _hwnd;
    private int _width;
    private int _height;
    private bool _ready;
    private bool _disposed;

    // The last picture drawn, to draw again after a resize. Valid only until the decoder is called again.
    private (ID3D11Texture2D Texture, int Width, int Height)? _lastPicture;

    /// <summary>Called on the window's thread for every message in the application range (<c>WM_APP</c> and above).</summary>
    public Action<uint>? AppMessage { get; set; }

    /// <summary>Called on the window's thread when handling a message threw; the window carries on.</summary>
    public Action<Exception>? Faulted { get; set; }

    /// <exception cref="NotSupportedException">The GPU's video processor cannot do what the window needs.</exception>
    public FullscreenRenderWindow(ID3D11Device device, bool windowed)
    {
        ArgumentNullException.ThrowIfNull(device);
        _device = device;
        _windowed = windowed;
        _blitter = new PictureBlitter(device);
        try
        {
            RegisterClassOnce();
            _self = GCHandle.Alloc(this);
            _hwnd = CreateWindow(windowed, GCHandle.ToIntPtr(_self));
            (_width, _height) = ReadClientSize();
            _swapChain = CreateSwapChain(device, _hwnd, _width, _height);
            _ready = true;
        }
        catch
        {
            Dispose();
            throw;
        }

        ShowWindow(_hwnd, SW_SHOW);
        SetForegroundWindow(_hwnd); // allowed or not as Windows sees fit; the window is topmost either way
    }

    /// <summary>Pumps this thread's messages until the window has been destroyed.</summary>
    public void RunMessageLoop()
    {
        while (GetMessageW(out var message, 0, 0, 0) > 0)
        {
            TranslateMessage(ref message);
            DispatchMessageW(ref message);
        }
    }

    /// <summary>
    /// Posts application message number <paramref name="number"/> (1 or more) to the window, to arrive at
    /// <see cref="AppMessage"/> on the window's thread. Safe from any thread; false when the window is gone.
    /// </summary>
    public bool PostAppMessage(uint number)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(number, 1u);
        return Post(WM_APP + number);
    }

    /// <summary>Closes the window, from any thread. Windowed or not, this closes it (the student's own request to does not, fullscreen).</summary>
    public bool PostClose() => Post(WM_APP);

    // A window handle of 0 would post to the calling thread's own queue, so a window that is gone is not posted to.
    private bool Post(uint message)
    {
        var hwnd = Volatile.Read(ref _hwnd);
        return hwnd != 0 && PostMessageW(hwnd, message, 0, 0);
    }

    /// <summary>Destroys the window. On the window's thread.</summary>
    public void Close()
    {
        if (_hwnd != 0)
        {
            DestroyWindow(_hwnd);
        }
    }

    /// <summary>
    /// Draws <paramref name="decodedTexture"/> (NV12; its top-left <paramref name="pictureWidth"/> by
    /// <paramref name="pictureHeight"/> pixels are the picture, the texture may be larger) onto the window,
    /// scaled to fit and centred, and presents it.
    /// </summary>
    public void PresentFrame(ID3D11Texture2D decodedTexture, int pictureWidth, int pictureHeight)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(decodedTexture);

        _lastPicture = (decodedTexture, pictureWidth, pictureHeight);
        Redraw();
    }

    /// <summary>Says that the decoder is about to be called again, so the last picture can no longer be drawn.</summary>
    public void ForgetPicture() => _lastPicture = null;

    private void Redraw()
    {
        if (!_ready || _lastPicture is not { } picture || _width <= 0 || _height <= 0)
        {
            return;
        }

        using (var backBuffer = DisplayStepException.Run("getting the swap chain's back buffer", () => _swapChain.GetBuffer<ID3D11Texture2D>(0)))
        {
            _blitter.Draw(picture.Texture, picture.Width, picture.Height, backBuffer, new PixelSize(_width, _height));
        }

        // No wait for the vertical blank: the picture is shown as soon as it is ready.
        DisplayStepException.Run("presenting the swap chain", () =>
        {
            var result = _swapChain.Present(0, PresentFlags.None);
            if (result.Failure)
            {
                result.CheckError();
            }
        });
    }

    private void ResizeTo(int width, int height)
    {
        // Windows sends WM_SIZE while the window is being created, before there is a swap chain to resize.
        if (!_ready || width <= 0 || height <= 0 || (width == _width && height == _height))
        {
            return;
        }

        DisplayStepException.Run(
            $"resizing the swap chain's buffers to {width}x{height}",
            () => _swapChain.ResizeBuffers(0, (uint)width, (uint)height, Format.Unknown, SwapChainFlags.None).CheckError());
        _width = width;
        _height = height;
        Redraw();
    }

    private (int Width, int Height) ReadClientSize()
    {
        GetClientRect(_hwnd, out var client);
        return (client.Right - client.Left, client.Bottom - client.Top);
    }

    private static void RegisterClassOnce()
    {
        lock (ClassGate)
        {
            if (s_classRegistered)
            {
                return;
            }

            var windowClass = new WindowClass
            {
                Size = (uint)Marshal.SizeOf<WindowClass>(),
                WindowProcedure = Marshal.GetFunctionPointerForDelegate(s_windowProcedure),
                Instance = GetModuleHandleW(null),
                Background = GetStockObject(BLACK_BRUSH), // black from the first instant, before anything is drawn
                ClassName = ClassName,
            };
            if (RegisterClassExW(ref windowClass) == 0)
            {
                throw new InvalidOperationException(
                    $"The render window class could not be registered (Win32 error {Marshal.GetLastWin32Error()}).");
            }

            s_classRegistered = true;
        }
    }

    private static nint CreateWindow(bool windowed, nint self)
    {
        int x, y, width, height;
        uint style, extendedStyle;
        if (windowed)
        {
            style = WS_OVERLAPPEDWINDOW;
            extendedStyle = 0;
            var frame = new Rect { Right = 960, Bottom = 540 }; // the picture's area; the frame comes on top of it
            AdjustWindowRectEx(ref frame, style, false, extendedStyle);
            (x, y, width, height) = (CW_USEDEFAULT, CW_USEDEFAULT, frame.Right - frame.Left, frame.Bottom - frame.Top);
        }
        else
        {
            // The primary display starts at (0, 0) by definition. No taskbar button either.
            style = WS_POPUP;
            extendedStyle = WS_EX_TOPMOST | WS_EX_TOOLWINDOW;
            (x, y, width, height) = (0, 0, GetSystemMetrics(SM_CXSCREEN), GetSystemMetrics(SM_CYSCREEN));
        }

        var hwnd = CreateWindowExW(
            extendedStyle, ClassName, windowed ? "Educast (windowed)" : null, style,
            x, y, width, height, 0, 0, GetModuleHandleW(null), self);
        if (hwnd == 0)
        {
            throw new InvalidOperationException($"The render window could not be created (Win32 error {Marshal.GetLastWin32Error()}).");
        }

        return hwnd;
    }

    private static IDXGISwapChain1 CreateSwapChain(ID3D11Device device, nint hwnd, int width, int height)
    {
        using var dxgiDevice = device.QueryInterface<IDXGIDevice>();
        using var adapter = dxgiDevice.GetAdapter();
        using var factory = adapter.GetParent<IDXGIFactory2>();

        var swapChain = factory.CreateSwapChainForHwnd(
            device,
            hwnd,
            new SwapChainDescription1
            {
                Width = (uint)width,
                Height = (uint)height,
                Format = Format.B8G8R8A8_UNorm,
                Stereo = false,
                SampleDescription = new SampleDescription(1, 0),
                BufferUsage = Usage.RenderTargetOutput,
                BufferCount = 2,
                Scaling = Scaling.Stretch,
                SwapEffect = SwapEffect.FlipDiscard,
                AlphaMode = AlphaMode.Ignore,
                Flags = SwapChainFlags.None,
            });

        // Nothing is to take the window into DXGI's exclusive fullscreen (Alt+Enter).
        factory.MakeWindowAssociation(hwnd, WindowAssociationFlags.IgnoreAll);
        return swapChain;
    }

    // ---- the window procedure ---------------------------------------------------------------------

    private static nint RouteMessage(nint hwnd, uint message, nint wParam, nint lParam)
    {
        if (message == WM_NCCREATE)
        {
            // CREATESTRUCT begins with lpCreateParams: the handle to the managed window being created.
            SetWindowLongPtr(hwnd, GWLP_USERDATA, Marshal.ReadIntPtr(lParam));
            if (GCHandle.FromIntPtr(Marshal.ReadIntPtr(lParam)).Target is FullscreenRenderWindow creating)
            {
                creating._hwnd = hwnd;
            }
        }

        var handle = GetWindowLongPtr(hwnd, GWLP_USERDATA);
        if (handle == 0 || GCHandle.FromIntPtr(handle).Target is not FullscreenRenderWindow window)
        {
            return DefWindowProcW(hwnd, message, wParam, lParam);
        }

        try
        {
            return window.HandleMessage(hwnd, message, wParam, lParam);
        }
        catch (Exception failure)
        {
            // An exception must never cross back into native code: it would end the process.
            window.Faulted?.Invoke(failure);
            return DefWindowProcW(hwnd, message, wParam, lParam);
        }
    }

    private nint HandleMessage(nint hwnd, uint message, nint wParam, nint lParam)
    {
        switch (message)
        {
            case >= WM_APP and <= 0xBFFF:
                // WM_APP itself is PostClose; the messages above it are the owner's.
                if (message == WM_APP)
                {
                    DestroyWindow(hwnd);
                }
                else
                {
                    AppMessage?.Invoke(message - WM_APP);
                }

                return 0;

            case WM_SETCURSOR when (lParam & 0xFFFF) == HTCLIENT:
                SetCursor(0); // the student's pointer is not shown over the picture
                return 1;

            case WM_ERASEBKGND:
                return 1; // the picture covers everything; erasing would flash

            case WM_PAINT:
                ValidateRect(hwnd, 0);
                return 0;

            case WM_CLOSE:
                if (_windowed)
                {
                    DestroyWindow(hwnd);
                }

                // Fullscreen: the student's Alt+F4 does nothing. The teacher ends the broadcast.
                return 0;

            case WM_SIZE when _windowed && (int)wParam != SIZE_MINIMIZED:
                var (width, height) = ReadClientSize();
                ResizeTo(width, height);
                return 0;

            case WM_DISPLAYCHANGE when !_windowed:
                // The student PC's resolution changed: cover the display as it is now.
                var (screenWidth, screenHeight) = (GetSystemMetrics(SM_CXSCREEN), GetSystemMetrics(SM_CYSCREEN));
                SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, screenWidth, screenHeight, SWP_NOACTIVATE);
                ResizeTo(screenWidth, screenHeight);
                return 0;

            case WM_DESTROY:
                Volatile.Write(ref _hwnd, 0);
                PostQuitMessage(0);
                return 0;

            case WM_NCDESTROY:
                SetWindowLongPtr(hwnd, GWLP_USERDATA, 0);
                return DefWindowProcW(hwnd, message, wParam, lParam);

            default:
                return DefWindowProcW(hwnd, message, wParam, lParam);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _lastPicture = null;
        if (_hwnd != 0)
        {
            DestroyWindow(_hwnd);
        }

        _swapChain?.Dispose();
        _blitter.Dispose();
        if (_self.IsAllocated)
        {
            _self.Free();
        }
    }
}
