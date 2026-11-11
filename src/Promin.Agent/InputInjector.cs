using System.Runtime.InteropServices;
using Promin.Protocol.Messages;
using static Promin.Agent.NativeMethods;

namespace Promin.Agent;

/// <summary>Input played on this PC for a console PC that controls it.</summary>
internal interface IInputInjector
{
    /// <summary>Moves the pointer to a position on the main screen, in 0 to 65535 across its width and height.</summary>
    void Move(ushort x, ushort y);

    void Button(MouseButton button, bool down);

    /// <summary>Turns the wheel by <paramref name="delta"/> (120 is one notch; positive is away from the user, or right when horizontal).</summary>
    void Wheel(int delta, bool horizontal);

    void Key(ushort virtualKey, ushort scanCode, bool extended, bool down);

    /// <summary>Releases every key and button this injector pressed and has not released. Sends nothing when there are none.</summary>
    void ReleaseAll();
}

/// <summary>
/// Plays input through a delegate, tags it with <see cref="InputBlock.OwnTag"/> so the input block lets it through, and
/// remembers what is held down so that <see cref="ReleaseAll"/> leaves nothing pressed. Safe to call from several threads.
/// </summary>
internal sealed class InputInjector(Action<Input[]> send) : IInputInjector
{
    private readonly Lock _gate = new();
    private readonly HashSet<(ushort VirtualKey, ushort ScanCode, bool Extended)> _keysDown = [];
    private readonly HashSet<MouseButton> _buttonsDown = [];

    public static InputInjector ForThisPc() =>
        new(inputs => SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>()));

    public void Move(ushort x, ushort y) =>
        Send(Mouse(MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE, 0, x, y));

    public void Button(MouseButton button, bool down)
    {
        lock (_gate)
        {
            _ = down ? _buttonsDown.Add(button) : _buttonsDown.Remove(button);
            Send(ButtonInput(button, down));
        }
    }

    public void Wheel(int delta, bool horizontal) =>
        Send(Mouse(horizontal ? MOUSEEVENTF_HWHEEL : MOUSEEVENTF_WHEEL, unchecked((uint)delta)));

    public void Key(ushort virtualKey, ushort scanCode, bool extended, bool down)
    {
        lock (_gate)
        {
            var key = (virtualKey, scanCode, extended);
            _ = down ? _keysDown.Add(key) : _keysDown.Remove(key);
            Send(KeyInput(key, down));
        }
    }

    public void ReleaseAll()
    {
        lock (_gate)
        {
            var ups = _keysDown.Select(key => KeyInput(key, down: false))
                .Concat(_buttonsDown.Select(button => ButtonInput(button, down: false)))
                .ToArray();
            _keysDown.Clear();
            _buttonsDown.Clear();
            if (ups.Length > 0)
            {
                send(ups);
            }
        }
    }

    private void Send(Input input) => send([input]);

    private static Input Mouse(uint flags, uint mouseData, int x = 0, int y = 0) => new()
    {
        Type = INPUT_MOUSE,
        Mouse = new MouseInput { Dx = x, Dy = y, MouseData = mouseData, Flags = flags, ExtraInfo = InputBlock.OwnTag },
    };

    private static Input ButtonInput(MouseButton button, bool down) => button switch
    {
        MouseButton.Left => Mouse(down ? MOUSEEVENTF_LEFTDOWN : MOUSEEVENTF_LEFTUP, 0),
        MouseButton.Right => Mouse(down ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_RIGHTUP, 0),
        MouseButton.Middle => Mouse(down ? MOUSEEVENTF_MIDDLEDOWN : MOUSEEVENTF_MIDDLEUP, 0),
        MouseButton.X1 => Mouse(down ? MOUSEEVENTF_XDOWN : MOUSEEVENTF_XUP, XBUTTON1),
        MouseButton.X2 => Mouse(down ? MOUSEEVENTF_XDOWN : MOUSEEVENTF_XUP, XBUTTON2),
        _ => throw new ArgumentOutOfRangeException(nameof(button)),
    };

    private static Input KeyInput((ushort VirtualKey, ushort ScanCode, bool Extended) key, bool down) => new()
    {
        Type = INPUT_KEYBOARD,
        Keyboard = new KeyboardInput
        {
            VirtualKey = key.VirtualKey,
            ScanCode = key.ScanCode,
            Flags = (key.Extended ? KEYEVENTF_EXTENDEDKEY : 0) | (down ? 0 : KEYEVENTF_KEYUP),
            ExtraInfo = InputBlock.OwnTag,
        },
    };
}
