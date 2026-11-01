namespace Promin.Agent.Tests.Hardware;

// This covers EVERY SCREEN with the real lock window for 15 s, on the PC of whoever is using it. Keyboard and mouse stay
// free: it has no input block. Run it by hand with the person looking (remove the Skip text, run, put it back), once on
// a 16:9 screen and once at 4:3. What it shows, 5 s each: a 100-character message wrapped to fit, the same as one
// unbroken word, broken to fit, and no message, with "This PC is locked" as the large line. Compare it with
// docs/superpowers/specs/2026-10-31-screen-lock-mockups/lock-screen-look.html, look A.
public class LockScreenWindowTests(ITestOutputHelper output)
{
    private const string NeedsAScreen = "Covers every screen for 15 s; run manually with the person looking.";

    private const string SpacedMessage =
        "Break until 11:30, then open the exercise on page 42 and work through it with the person next to you";

    [Fact(Skip = NeedsAScreen)]
    public async Task EachMessage_ShowsFor5Seconds_ThenHides()
    {
        using var screen = new LockScreenWindow(output.WriteLine);

        foreach (var message in new[] { SpacedMessage, new string('W', SpacedMessage.Length), "" })
        {
            Assert.True(screen.Show(message));
            await Task.Delay(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            screen.Hide();
        }
    }
}
