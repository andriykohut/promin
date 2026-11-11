using Promin.Master.Core.Control;
using Promin.Protocol.Messages;

namespace Promin.Master.Core.Tests.Control;

public class HeldKeysTests
{
    private static KeyInput Key(ushort vk, bool down) => new(vk, 0, false, down);

    [Fact]
    public void ReleaseAll_GivesAnUpForEachKeyStillDown()
    {
        var held = new HeldKeys();
        held.Down(Key(0x12, true));
        held.Down(Key(0x41, true));
        held.Up(Key(0x41, false));

        Assert.Equal([Key(0x12, false)], held.ReleaseAll());
        Assert.Empty(held.ReleaseAll());
    }

    [Fact]
    public void ARepeatedDown_IsReleasedOnce()
    {
        var held = new HeldKeys();
        held.Down(Key(0x41, true));
        held.Down(Key(0x41, true));

        Assert.Single(held.ReleaseAll());
    }
}
