using Promin.Protocol.Messages;
using static Promin.Agent.NativeMethods;

namespace Promin.Agent.Tests;

// The send delegate records the arrays; nothing here reaches SendInput.
public class InputInjectorTests
{
    private readonly List<Input[]> _sent = [];

    private InputInjector Injector() => new(_sent.Add);

    private IEnumerable<Input> All => _sent.SelectMany(batch => batch);

    [Fact]
    public void TheInputStruct_IsTheSizeOf64BitWindowsINPUT() =>
        Assert.Equal(40, System.Runtime.InteropServices.Marshal.SizeOf<Input>());

    [Fact]
    public void EveryEvent_CarriesOurTag()
    {
        var injector = Injector();

        injector.Move(1, 2);
        injector.Button(MouseButton.X2, down: true);
        injector.Wheel(-120, horizontal: true);
        injector.Key(0x41, 0x1E, extended: false, down: true);
        injector.ReleaseAll();

        Assert.Equal(6, All.Count());
        Assert.All(All, input =>
            Assert.Equal(InputBlock.OwnTag, input.Type == INPUT_MOUSE ? input.Mouse.ExtraInfo : input.Keyboard.ExtraInfo));
    }

    [Fact]
    public void Move_IsAbsoluteOnTheMainScreen()
    {
        Injector().Move(100, 200);

        var mouse = Assert.Single(All).Mouse;
        Assert.Equal(MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE, mouse.Flags);
        Assert.Equal((100, 200), (mouse.Dx, mouse.Dy));
    }

    [Fact]
    public void Wheel_UsesTheHorizontalFlagOnlyWhenAsked()
    {
        var injector = Injector();

        injector.Wheel(120, horizontal: false);
        injector.Wheel(-120, horizontal: true);

        Assert.Equal([MOUSEEVENTF_WHEEL, MOUSEEVENTF_HWHEEL], All.Select(input => input.Mouse.Flags));
        Assert.Equal(-120, (int)All.Last().Mouse.MouseData);
    }

    [Fact]
    public void Key_Extended_SetsTheExtendedFlag()
    {
        var injector = Injector();

        injector.Key(0x25, 0x4B, extended: true, down: true);
        injector.Key(0x25, 0x4B, extended: true, down: false);

        Assert.Equal([KEYEVENTF_EXTENDEDKEY, KEYEVENTF_EXTENDEDKEY | KEYEVENTF_KEYUP], All.Select(input => input.Keyboard.Flags));
    }

    [Fact]
    public void ReleaseAll_ReleasesEveryKeyAndButtonStillDown_Once()
    {
        var injector = Injector();
        injector.Key(0x11, 0x1D, extended: false, down: true);
        injector.Button(MouseButton.Left, down: true);
        injector.Key(0x41, 0x1E, extended: false, down: true);
        injector.Key(0x41, 0x1E, extended: false, down: false);
        _sent.Clear();

        injector.ReleaseAll();
        var released = All.ToList();
        injector.ReleaseAll();

        Assert.Equal(2, released.Count);
        Assert.Contains(released, input => input.Type == INPUT_KEYBOARD && input.Keyboard.VirtualKey == 0x11 && input.Keyboard.Flags == KEYEVENTF_KEYUP);
        Assert.Contains(released, input => input.Type == INPUT_MOUSE && input.Mouse.Flags == MOUSEEVENTF_LEFTUP);
        Assert.Single(_sent);
    }

    [Fact]
    public void ADownForSomethingAlreadyDown_IsNotCountedTwice()
    {
        var injector = Injector();
        injector.Key(0x41, 0x1E, extended: false, down: true);
        injector.Key(0x41, 0x1E, extended: false, down: true);
        injector.Button(MouseButton.Right, down: true);
        injector.Button(MouseButton.Right, down: true);
        _sent.Clear();

        injector.ReleaseAll();

        Assert.Equal(2, All.Count());
    }
}
