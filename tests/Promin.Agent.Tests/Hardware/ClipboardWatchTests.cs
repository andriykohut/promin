using System.Runtime.InteropServices;
using static Promin.Agent.NativeMethods;

namespace Promin.Agent.Tests.Hardware;

// These replace whatever is on the clipboard of whoever is using the PC. They move no mouse and send no keys. Run them
// by hand on the target machine (remove the Skip text, run, put it back).
public class ClipboardWatchTests(ITestOutputHelper output)
{
    private const string NeedsAClipboard = "Overwrites the real clipboard; run manually on target hardware.";

    private static string? ReadClipboard()
    {
        if (!OpenClipboard(0))
        {
            return null;
        }

        try
        {
            var data = GetClipboardData(CF_UNICODETEXT);
            if (data == 0)
            {
                return null;
            }

            var text = Marshal.PtrToStringUni(GlobalLock(data));
            GlobalUnlock(data);
            return text;
        }
        finally
        {
            CloseClipboard();
        }
    }

    [Fact(Skip = NeedsAClipboard)]
    public async Task AWrittenText_IsOnTheClipboard_AndIsNotReportedAsCopied()
    {
        using var watch = new ClipboardWatch(output.WriteLine);
        var copied = new List<string>();
        watch.Copied += text => { lock (copied) { copied.Add(text); } };

        watch.Write("Promin proof");
        await Task.Delay(500, TestContext.Current.CancellationToken);

        Assert.Equal("Promin proof", ReadClipboard());
        lock (copied)
        {
            Assert.Empty(copied);
        }
    }

    [Fact(Skip = NeedsAClipboard)]
    public async Task ATextCopiedElsewhere_IsReportedOnTheWatchsThread()
    {
        using var watch = new ClipboardWatch(output.WriteLine);
        using var other = new ClipboardWatch(output.WriteLine);
        var copied = new TaskCompletionSource<string>();
        var watchThread = 0;
        watch.Copied += text =>
        {
            watchThread = Environment.CurrentManagedThreadId;
            copied.TrySetResult(text);
        };

        other.Write("copied elsewhere");

        Assert.Equal("copied elsewhere", await copied.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.NotEqual(Environment.CurrentManagedThreadId, watchThread);
    }
}
