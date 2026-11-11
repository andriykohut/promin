using Promin.Master.Core.Control;

namespace Promin.Master.Core.Tests.Control;

public class ControlKeysTests
{
    [Theory]
    [InlineData(0x09, true, false, true, true)]
    [InlineData(0x09, true, false, false, false)]
    [InlineData(0x5B, false, false, false, false)]
    [InlineData(0x5C, false, false, true, true)]
    [InlineData(0x1B, false, true, false, false)]
    [InlineData(0x1B, true, false, false, false)]
    [InlineData(0x09, false, false, false, true)]
    [InlineData(0x41, true, false, false, true)]
    public void GoesToRemote(int vk, bool alt, bool ctrl, bool fullScreen, bool expected) =>
        Assert.Equal(expected, ControlKeys.GoesToRemote(vk, alt, ctrl, fullScreen));
}
