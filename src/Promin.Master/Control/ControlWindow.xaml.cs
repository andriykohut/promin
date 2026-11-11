using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Promin.Master.Core;
using Promin.Master.Core.Control;
using Promin.Protocol.Messages;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Graphics;

namespace Promin.Master.Control;

/// <summary>
/// Shows the agent PC under control and passes it this PC's pointer, keys and clipboard text. It opens full screen
/// on the monitor of the console's main window and switches to a window of its own and back without interrupting
/// control; the window form's last place is kept in <see cref="Core.Wall.IWallSettings.ControlWindowPlacement"/>.
/// The window owns the session: Stop controlling, closing the window, <see cref="End"/>, the main window closing and
/// the session ending by itself all close the window and dispose the session, then raise <see cref="Ended"/> once.
/// </summary>
internal sealed partial class ControlWindow : Window
{
    private const double TopEdge = 2;
    private const int BesideWidth = 1280;
    private const int BesideHeight = 800;

    private static readonly TimeSpan TuckDelay = TimeSpan.FromSeconds(3);

    private readonly IControlSession _session;
    private readonly string _pcName;
    private readonly AppWindow _mainWindow;
    private readonly RemoteVideo _video;
    private readonly KeyboardCapture _keyboard;
    private readonly ClipboardRelay _clipboard = new();
    private readonly DispatcherQueueTimer _tuck;
    private readonly CancellationTokenSource _closing = new();

    // One sender, so messages reach the agent PC in the order they were made, and the keyboard hook never waits on the network.
    private readonly Channel<ControlMessage> _outbox = Channel.CreateUnbounded<ControlMessage>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });

    // The mouse buttons sent down to the agent PC and not yet up.
    private readonly HashSet<MouseButton> _buttonsDown = [];

    private Task _videoRun = Task.CompletedTask;
    private bool _focused;
    private bool _pointerHidden;
    private bool _finished;
    private bool _mayClose;

    /// <summary>
    /// Builds the window on <paramref name="session"/>; nothing is shown or started until <see cref="Open"/>. Throws when
    /// the picture cannot be set up, leaving the session to the caller.
    /// </summary>
    public ControlWindow(IControlSession session, string pcName, AppWindow mainWindow)
    {
        _session = session;
        _pcName = pcName;
        _mainWindow = mainWindow;
        InitializeComponent();

        Title = Phrase.Format(Strings.Control_WindowTitle, ("name", pcName));
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        StripName.Text = BarName.Text = pcName;
        try
        {
            _video = new RemoteVideo(Picture, message => Trace.TraceWarning(message));
        }
        catch
        {
            // A window left open, even one never shown, keeps the app running after the main window has closed.
            Close();
            throw;
        }

        _keyboard = new KeyboardCapture(Send);
        _tuck = DispatcherQueue.CreateTimer();
        _tuck.Interval = TuckDelay;
        _tuck.IsRepeating = false;
        _tuck.Tick += (_, _) => Tuck(away: true);
    }

    /// <summary>Raised once, on the UI thread, after the window has closed and the session is disposed: true when the session ended by itself (its stream or the connection is gone).</summary>
    public event Action<bool>? Ended;

    /// <summary>
    /// Shows the window full screen on the monitor the main window is on and starts control. Call once, after
    /// subscribing to <see cref="Ended"/>: from here every end, a failure to show the window included, raises it.
    /// </summary>
    public void Open()
    {
        Trace.TraceInformation("Controlling {0}.", _pcName);
        try
        {
            Activated += OnActivated;
            Closed += OnClosed;
            Clipboard.ContentChanged += OnClipboardChanged;
            _mainWindow.Destroying += OnMainWindowDestroying;

            // Full screen fills the monitor the window is on, and this place is on the main window's.
            AppWindow.MoveAndResize(BesideMainWindow());
            ShowFullScreen(true);
            Activate();
        }
        catch (Exception e)
        {
            Finish(lost: false, $"the window could not be shown ({e.GetType().Name})");
            return;
        }

        _ = Task.Run(SendAllAsync);
        Run();
    }

    /// <summary>
    /// Ends control, as the room asks when this PC is locked, removed or replaced by another: closes the window and
    /// disposes the session. <see cref="Ended"/> still says lost when the session had already ended by itself.
    /// </summary>
    public void End()
    {
        // A lost connection stops this video and the room's pictures together (AgentStreamDemux), and the room asks
        // through the UI thread's queue, so the video has stopped by then unless its own thread has yet to run.
        if (_videoRun.IsCompleted)
        {
            Finish(lost: true, "the connection was lost");
        }
        else
        {
            Finish(lost: false, "ended for a lock, a removal or control of another PC");
        }
    }

    // Ends control when the video or the messages stop by themselves: the stream or the connection is gone.
    private async void Run()
    {
        _videoRun = _video.RunAsync(_session, _closing.Token);
        var stopped = await Task.WhenAny(_videoRun, ReceiveMessagesAsync(_closing.Token));
        if (!_closing.IsCancellationRequested)
        {
            Finish(lost: true, stopped.Exception is { } failure
                ? $"the connection was lost ({failure.GetBaseException().GetType().Name})"
                : "the connection was lost");
        }
    }

    private async Task ReceiveMessagesAsync(CancellationToken ct)
    {
        while (true)
        {
            switch (await _session.ReceiveAsync(ct))
            {
                case ControlState { Status: ControlStatus.ProtectedScreen }:
                    ShowProtectedScreen(true);
                    break;
                case ControlState { Status: ControlStatus.Desktop }:
                    ShowProtectedScreen(false);
                    break;
                case ClipboardText { Text: var text }:
                    PutOnClipboard(text);
                    break;
            }
        }
    }

    private void Send(ControlMessage message) => _outbox.Writer.TryWrite(message);

    private async Task SendAllAsync()
    {
        try
        {
            await foreach (var message in _outbox.Reader.ReadAllAsync())
            {
                await _session.SendAsync(message, CancellationToken.None);
            }
        }
        catch (Exception)
        {
            // The session has ended; Run reports that.
        }
    }

    /// <summary>
    /// The one way control ends, run at most once: the keys and buttons still down are let go, the window closes once
    /// the picture is off it, then the session goes and <see cref="Ended"/> is raised, whichever earlier step failed.
    /// </summary>
    private async void Finish(bool lost, string why)
    {
        if (_finished)
        {
            return;
        }

        _finished = true;
        Step(() =>
        {
            _keyboard.Dispose();
            ReleaseButtons();
        });
        _outbox.Writer.TryComplete();
        Step(RememberPlace);
        Step(() => Clipboard.ContentChanged -= OnClipboardChanged);
        Step(() => _mainWindow.Destroying -= OnMainWindowDestroying);
        Step(_tuck.Stop);
        Step(_closing.Cancel);
        try
        {
            await _videoRun;
        }
        catch (Exception)
        {
            // Cancelled, or the session's own end, which why already gives.
        }

        Step(_video.Dispose);
        Step(() => ShowPointer(true));
        Step(() =>
        {
            _mayClose = true;
            Close();
        });
        try
        {
            await _session.DisposeAsync();
        }
        catch (Exception e)
        {
            Trace.TraceWarning("Closing the control session of {0} failed: {1}.", _pcName, e.GetType().Name);
        }

        Trace.TraceInformation("Control of {0} ended: {1}.", _pcName, why);
        Ended?.Invoke(lost);
    }

    // A step of Finish that fails is logged and the rest still run: the session must be disposed and Ended raised.
    private void Step(Action step)
    {
        try
        {
            step();
        }
        catch (Exception e)
        {
            Trace.TraceWarning("A step of ending control of {0} failed: {1}.", _pcName, e.GetType().Name);
        }
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        if (_mayClose)
        {
            return;
        }

        // The window stays until the picture is off it; Finish closes it then, outside this event.
        args.Handled = true;
        DispatcherQueue.TryEnqueue(() => Finish(lost: false, "the window was closed"));
    }

    private void OnMainWindowDestroying(AppWindow sender, object args) => Finish(lost: false, "the console was closed");

    private void Stop_Click(object sender, RoutedEventArgs e) => Finish(lost: false, "stopped");

    private void InWindow_Click(object sender, RoutedEventArgs e) => ShowFullScreen(false);

    private void FullScreen_Click(object sender, RoutedEventArgs e)
    {
        RememberPlace();
        ShowFullScreen(true);
    }

    private void ShowFullScreen(bool fullScreen)
    {
        if (fullScreen)
        {
            AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
        }
        else
        {
            AppWindow.SetPresenter(AppWindowPresenterKind.Overlapped);
            AppWindow.MoveAndResize(RememberedPlace() ?? BesideMainWindow());
        }

        _keyboard.FullScreen = fullScreen;
        Strip.Visibility = fullScreen ? Visibility.Collapsed : Visibility.Visible;
        Bar.Visibility = fullScreen ? Visibility.Visible : Visibility.Collapsed;
        if (fullScreen)
        {
            Tuck(away: false);
        }
        else
        {
            _tuck.Stop();
            Handle.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>Slides the full-screen bar out of sight, leaving the handle, or brings it back to tuck away after <see cref="TuckDelay"/>.</summary>
    private void Tuck(bool away)
    {
        Bar.Translation = away ? new Vector3(0, -(float)Bar.ActualHeight, 0) : Vector3.Zero;
        Bar.IsHitTestVisible = !away;
        Handle.Visibility = away ? Visibility.Visible : Visibility.Collapsed;
        if (!away)
        {
            TuckLater();
        }
    }

    private void TuckLater()
    {
        _tuck.Stop();
        _tuck.Start();
    }

    private void Bar_PointerEntered(object sender, PointerRoutedEventArgs e) => _tuck.Stop();

    private void Bar_PointerExited(object sender, PointerRoutedEventArgs e) => TuckLater();

    /// <summary>Keeps the window form's place for next time, when it is showing and neither minimised nor maximised.</summary>
    private void RememberPlace()
    {
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Restored })
        {
            var (position, size) = (AppWindow.Position, AppWindow.Size);
            App.Services.WallSettings.ControlWindowPlacement =
                string.Create(CultureInfo.InvariantCulture, $"{position.X},{position.Y},{size.Width},{size.Height}");
        }
    }

    /// <summary>The window form's remembered place; null when there is none, or no screen shows any of it now.</summary>
    private static RectInt32? RememberedPlace()
    {
        var values = (App.Services.WallSettings.ControlWindowPlacement ?? string.Empty).Split(',')
            .Select(part => int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : (int?)null)
            .ToArray();
        if (values is not [{ } x, { } y, { } width, { } height] || width <= 0 || height <= 0)
        {
            return null;
        }

        var place = new RectInt32(x, y, width, height);
        return DisplayArea.GetFromRect(place, DisplayAreaFallback.None) is null ? null : place;
    }

    /// <summary>1280 by 800 effective pixels to the right of the main window, moved and shrunk as needed to fit its screen.</summary>
    private RectInt32 BesideMainWindow()
    {
        var screen = DisplayArea.GetFromWindowId(_mainWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
        var scale = GetDpiForWindow(Win32Interop.GetWindowFromWindowId(_mainWindow.Id)) / 96.0;
        var width = Math.Min((int)(BesideWidth * scale), screen.Width);
        var height = Math.Min((int)(BesideHeight * scale), screen.Height);
        return new RectInt32(
            Math.Clamp(_mainWindow.Position.X + _mainWindow.Size.Width, screen.X, screen.X + screen.Width - width),
            Math.Clamp(_mainWindow.Position.Y, screen.Y, screen.Y + screen.Height - height),
            width,
            height);
    }

    private async void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        _focused = args.WindowActivationState != WindowActivationState.Deactivated;
        if (!_focused || _finished)
        {
            _keyboard.Stop();
            ReleaseButtons();
            return;
        }

        _keyboard.Start();
        if (_clipboard.OnViewFocused(await ClipboardTextAsync()) is { } text)
        {
            Send(new ClipboardText(text));
        }
    }

    // The event may come on any thread; the clipboard is read, and the relay used, on the UI thread.
    private void OnClipboardChanged(object? sender, object e) => DispatcherQueue.TryEnqueue(async () =>
    {
        if (_clipboard.OnLocalCopy(await ClipboardTextAsync(), _focused) is { } text)
        {
            Send(new ClipboardText(text));
        }
    });

    /// <summary>This PC's clipboard text; null when it holds none or cannot be read now. The text is never logged.</summary>
    private static async Task<string?> ClipboardTextAsync()
    {
        try
        {
            var content = Clipboard.GetContent();
            return content.Contains(StandardDataFormats.Text) ? await content.GetTextAsync() : null;
        }
        catch (Exception e)
        {
            // Another app can hold the clipboard open for a moment; the text goes over on its next change or focus.
            Trace.TraceWarning("The clipboard could not be read: {0}.", e.GetType().Name);
            return null;
        }
    }

    private void PutOnClipboard(string text)
    {
        _clipboard.OnRemoteText(text);
        try
        {
            var package = new DataPackage();
            package.SetText(text);
            Clipboard.SetContent(package);
        }
        catch (Exception e)
        {
            Trace.TraceWarning("The other PC's clipboard text could not be put on this PC's clipboard: {0}.", e.GetType().Name);
        }
    }

    private void ShowProtectedScreen(bool showing)
    {
        ProtectedLayer.Visibility = showing ? Visibility.Visible : Visibility.Collapsed;
        _video.ShowStill(showing);
    }

    private void Picture_SizeChanged(object sender, SizeChangedEventArgs e) => ResizePicture();

    private void Picture_CompositionScaleChanged(SwapChainPanel sender, object args) => ResizePicture();

    private void ResizePicture() =>
        _video.Resize(Picture.ActualWidth, Picture.ActualHeight, Picture.CompositionScaleX, Picture.CompositionScaleY);

    private void Picture_PointerEntered(object sender, PointerRoutedEventArgs e) => ShowPointer(false);

    private void Picture_PointerExited(object sender, PointerRoutedEventArgs e) => ShowPointer(true);

    // ShowCursor keeps a count, so each hide is matched by exactly one show.
    private void ShowPointer(bool show)
    {
        if (_pointerHidden == show)
        {
            ShowCursor(show);
            _pointerHidden = !show;
        }
    }

    // Pressed, moved and released alike: a second button pressed or released while another is down comes as a move.
    private void Picture_Pointer(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(Picture);
        if (_keyboard.FullScreen && point.Position.Y < TopEdge)
        {
            Tuck(away: false);
        }

        var at = ToRemote(point.Position);
        if (at is { } remote)
        {
            Send(new PointerMove(remote.X, remote.Y));
        }

        // A press outside the picture is not the other PC's; a release always is, so no button stays down there.
        if (ButtonChange(point.Properties.PointerUpdateKind) is { } change && (!change.Down || at is not null))
        {
            if (change.Down)
            {
                Picture.CapturePointer(e.Pointer);
                _buttonsDown.Add(change.Button);
            }
            else
            {
                _buttonsDown.Remove(change.Button);
            }

            Send(new PointerButton(change.Button, change.Down));
        }
    }

    // After a normal release nothing is left down; a capture taken away mid-press leaves buttons down over there.
    private void Picture_PointerCaptureLost(object sender, PointerRoutedEventArgs e) => ReleaseButtons();

    /// <summary>Sends an up for every mouse button still down on the agent PC.</summary>
    private void ReleaseButtons()
    {
        foreach (var button in _buttonsDown)
        {
            Send(new PointerButton(button, Down: false));
        }

        _buttonsDown.Clear();
    }

    private void Picture_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(Picture);
        if (ToRemote(point.Position) is not null)
        {
            Send(new PointerWheel(point.Properties.MouseWheelDelta, point.Properties.IsHorizontalMouseWheel));
        }
    }

    // Null outside the picture, before the first picture, and while the panel has no size (minimised).
    private (ushort X, ushort Y)? ToRemote(Point position) =>
        Picture.ActualWidth > 0 && Picture.ActualHeight > 0 && _video.PictureSize is { } picture
            ? ControlGeometry.ToRemote(position.X, position.Y, Picture.ActualWidth, Picture.ActualHeight, picture)
            : null;

    private static (MouseButton Button, bool Down)? ButtonChange(PointerUpdateKind kind) => kind switch
    {
        PointerUpdateKind.LeftButtonPressed => (MouseButton.Left, true),
        PointerUpdateKind.LeftButtonReleased => (MouseButton.Left, false),
        PointerUpdateKind.RightButtonPressed => (MouseButton.Right, true),
        PointerUpdateKind.RightButtonReleased => (MouseButton.Right, false),
        PointerUpdateKind.MiddleButtonPressed => (MouseButton.Middle, true),
        PointerUpdateKind.MiddleButtonReleased => (MouseButton.Middle, false),
        PointerUpdateKind.XButton1Pressed => (MouseButton.X1, true),
        PointerUpdateKind.XButton1Released => (MouseButton.X1, false),
        PointerUpdateKind.XButton2Pressed => (MouseButton.X2, true),
        PointerUpdateKind.XButton2Released => (MouseButton.X2, false),
        _ => null,
    };

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);

    [DllImport("user32.dll")]
    private static extern int ShowCursor([MarshalAs(UnmanagedType.Bool)] bool show);
}
