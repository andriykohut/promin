namespace Promin.Agent.Tests;

// Only the block's decisions. Nothing here may call Hold: it would take the keyboard and mouse of the PC running the tests.
public class InputBlockTests
{
    private static readonly InputBlock.Hooks Old = new(1, 2);
    private static readonly InputBlock.Hooks Fresh = new(3, 4);

    [Theory]
    [InlineData(0x0101)] // WM_KEYUP
    [InlineData(0x0105)] // WM_SYSKEYUP
    [InlineData(0x0202)] // WM_LBUTTONUP
    [InlineData(0x0205)] // WM_RBUTTONUP
    [InlineData(0x0208)] // WM_MBUTTONUP
    [InlineData(0x020C)] // WM_XBUTTONUP
    public void AReleaseGoesThrough(int inputEvent) => Assert.True(InputBlock.LetsThrough(inputEvent));

    [Theory]
    [InlineData(0x0100)] // WM_KEYDOWN
    [InlineData(0x0104)] // WM_SYSKEYDOWN
    [InlineData(0x0200)] // WM_MOUSEMOVE
    [InlineData(0x0201)] // WM_LBUTTONDOWN
    [InlineData(0x0204)] // WM_RBUTTONDOWN
    [InlineData(0x0207)] // WM_MBUTTONDOWN
    [InlineData(0x020A)] // WM_MOUSEWHEEL
    [InlineData(0x020B)] // WM_XBUTTONDOWN
    [InlineData(0x020E)] // WM_MOUSEHWHEEL
    public void EverythingElseIsSwallowed(int inputEvent) => Assert.False(InputBlock.LetsThrough(inputEvent));

    [Fact]
    public void Renew_PutsTheFreshHooksIn_BeforeTakingTheOldOnesOut()
    {
        var order = new List<string>();

        var kept = InputBlock.Renew(
            Old,
            () =>
            {
                order.Add("install");
                return Fresh;
            },
            hooks => order.Add($"remove {hooks.Keyboard}"));

        Assert.Equal(["install", "remove 1"], order);
        Assert.Equal(Fresh, kept);
    }

    [Fact]
    public void Renew_KeepsTheOldHooks_WhenFreshOnesCannotBeMade()
    {
        var removed = false;

        var kept = InputBlock.Renew(Old, () => null, _ => removed = true);

        Assert.Equal(Old, kept);
        Assert.False(removed);
    }
}
