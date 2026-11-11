using System.Threading.Channels;
using Promin.Protocol.Messages;
using Promin.Protocol.Pairing;

namespace Promin.Agent.Tests;

/// <summary>The remote stream as the agent handles it, over a fake channel, fake parts and a <see cref="ScreenLock"/> on fakes.</summary>
public class RemoteLinkTests
{
    private sealed class FakeChannel : IControlChannel
    {
        public Channel<ControlMessage> FromConsole { get; } = Channel.CreateUnbounded<ControlMessage>();
        public Channel<ControlMessage> ToConsole { get; } = Channel.CreateUnbounded<ControlMessage>();

        public Task SendAsync(ControlMessage message, CancellationToken ct) => ToConsole.Writer.WriteAsync(message, ct).AsTask();

        public Task<ControlMessage> ReceiveAsync(CancellationToken ct) => FromConsole.Reader.ReadAsync(ct).AsTask();
    }

    private sealed class FakeConnection : IAgentConnection
    {
        public Channel<IControlChannel> Streams { get; } = Channel.CreateUnbounded<IControlChannel>();

        public Task<IControlChannel> AcceptRemoteStreamAsync(CancellationToken ct) => Streams.Reader.ReadAsync(ct).AsTask();

        public Task<IControlChannel> AcceptLockStreamAsync(CancellationToken ct) => throw new NotSupportedException();

        public Task<IIncomingSend> AcceptSendAsync(CancellationToken ct) => throw new NotSupportedException();

        public Task<ControlMessage> ReceiveControlAsync(CancellationToken ct) => throw new NotSupportedException();

        public ValueTask<byte[]> ReceiveVideoFrameAsync(CancellationToken ct) => throw new NotSupportedException();

        public Task SendControlAsync(ControlMessage message, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class Rig : IDisposable
    {
        private readonly TempDirectory _dir = new();
        private readonly CancellationTokenSource _stop = new(TimeSpan.FromSeconds(20));
        private readonly List<string> _log = [];
        private readonly FakeConnection _connection = new();
        private bool _onDesktop = true;

        public Rig(bool mayControl = true, bool withLock = true, bool canHold = true, bool locked = false, bool withParts = true)
        {
            Input = new FakeInput(Calls) { CanHold = canHold };
            Screen = new ScreenLock(
                new LockFile(Path.Combine(_dir.Path, "lock"), _log.Add), new FakeScreen(Calls), Input, new LockLog(_log.Add));
            Tab = new FakeTab(Calls);
            Clipboard = new FakeClipboard(Calls);
            Video = new FakeVideo(Calls);
            Injector = new FakeInjector(Calls);
            Previews = new FakePreviews(Calls);
            if (locked)
            {
                Screen.Lock("x");
                Calls.Clear();
            }

            var parts = new RemoteParts(
                _ =>
                {
                    Calls.Add("video");
                    return FailVideoOpen ? throw new InvalidOperationException("no capture") : Video;
                },
                () => Tab,
                () =>
                {
                    Calls.Add("clipboard");
                    return Clipboard;
                },
                Injector,
                () => _onDesktop,
                TimeSpan.FromMilliseconds(10));
            Link = new RemoteLink(withParts ? parts : null, withLock ? Screen : null, Previews, mayControl, _log.Add);
        }

        public List<string> Calls { get; } = [];
        public bool FailVideoOpen { get; set; }
        public FakeInput Input { get; }
        public ScreenLock Screen { get; }
        public FakeTab Tab { get; }
        public FakeClipboard Clipboard { get; }
        public FakeVideo Video { get; }
        public FakeInjector Injector { get; }
        public FakePreviews Previews { get; }
        public Task? Running { get; private set; }
        public RemoteLink Link { get; }
        public IReadOnlyList<string> Log => _log;

        public void OnDesktop(bool onDesktop) => _onDesktop = onDesktop;

        public void Start() => Running = Link.RunAsync(_connection, (_, _) => ValueTask.CompletedTask, _stop.Token);

        public void Cancel() => _stop.Cancel();

        public FakeChannel Open()
        {
            var channel = new FakeChannel();
            _connection.Streams.Writer.TryWrite(channel);
            return channel;
        }

        public async Task<ControlState> NextStateAsync(FakeChannel channel) =>
            Assert.IsType<ControlState>(await channel.ToConsole.Reader.ReadAsync(_stop.Token));

        public async Task<FakeChannel> StartedAsync()
        {
            Start();
            var channel = Open();
            Assert.Equal(ControlStatus.Started, (await NextStateAsync(channel)).Status);
            return channel;
        }

        public async Task WaitForAsync(Func<bool> condition)
        {
            for (var i = 0; i < 500 && !condition(); i++)
            {
                await Task.Delay(10, TestContext.Current.CancellationToken);
            }

            Assert.True(condition());
        }

        public void Dispose()
        {
            _stop.Cancel();
            Screen.Dispose();
            _stop.Dispose();
            _dir.Dispose();
        }
    }

    [Fact]
    public async Task Start_PausesPreviews_OpensVideo_ShowsTheTab_HoldsInput_ThenReportsStarted()
    {
        using var rig = new Rig();

        await rig.StartedAsync();

        Assert.Equal(["pause", "video", "tab:show", "hold", "clipboard"], rig.Calls);
    }

    [Fact]
    public async Task Start_FromTheConsoleOnThisPc_IsRefused()
    {
        using var rig = new Rig(mayControl: false);
        rig.Start();
        var channel = rig.Open();

        Assert.Equal(ControlStatus.Refused, (await rig.NextStateAsync(channel)).Status);
        Assert.Empty(rig.Calls);
    }

    [Fact]
    public async Task Start_WithoutALock_IsRefused()
    {
        using var rig = new Rig(withLock: false);
        rig.Start();
        var channel = rig.Open();

        Assert.Equal(ControlStatus.Refused, (await rig.NextStateAsync(channel)).Status);
        Assert.Empty(rig.Calls);
    }

    [Fact]
    public async Task Start_WithoutParts_IsRefused_AndNothingIsPausedOrOpened()
    {
        using var rig = new Rig(withParts: false);
        rig.Start();
        var channel = rig.Open();

        Assert.Equal(ControlStatus.Refused, (await rig.NextStateAsync(channel)).Status);
        Assert.Empty(rig.Calls);
    }

    [Theory]
    [InlineData("injector")]
    [InlineData("clipboard")]
    public async Task AStepOfEndingThatThrows_DoesNotStopTheRest(string throwing)
    {
        using var rig = new Rig();
        rig.Injector.ThrowOnRelease = throwing == "injector";
        rig.Clipboard.ThrowOnDispose = throwing == "clipboard";
        var channel = await rig.StartedAsync();
        rig.Calls.Clear();

        channel.FromConsole.Writer.Complete();
        await rig.WaitForAsync(() => rig.Calls.Contains("resume"));

        Assert.Equal(["release-all", "clipboard:dispose", "release", "tab:hide", "video:dispose", "resume"], rig.Calls);
        Assert.False(rig.Input.Held);
        Assert.Contains(rig.Log, line => line.Contains("InvalidOperationException"));
        Assert.False(rig.Running!.IsFaulted);
        Assert.Equal(ControlStatus.Started, (await rig.NextStateAsync(rig.Open())).Status);
    }

    [Fact]
    public async Task Start_WithABroadcastOnScreen_IsRefused_AndUndoesTheRest()
    {
        using var rig = new Rig(canHold: false);
        rig.Start();
        var channel = rig.Open();

        Assert.Equal(ControlStatus.Refused, (await rig.NextStateAsync(channel)).Status);
        await rig.WaitForAsync(() => rig.Calls.Contains("resume"));
        Assert.Equal(["pause", "video", "tab:show", "hold", "tab:hide", "video:dispose", "resume"], rig.Calls);
    }

    [Theory]
    [InlineData("video")]
    [InlineData("tab")]
    public async Task Start_WhenAPartFails_IsRefused_AndUndoesWhatWasDone(string failing)
    {
        using var rig = new Rig();
        rig.FailVideoOpen = failing == "video";
        rig.Tab.CanShow = failing != "tab";
        rig.Start();
        var channel = rig.Open();

        Assert.Equal(ControlStatus.Refused, (await rig.NextStateAsync(channel)).Status);
        await rig.WaitForAsync(() => rig.Calls.Contains("resume"));
        Assert.Equal(
            failing == "video"
                ? ["pause", "video", "resume"]
                : ["pause", "video", "tab:show", "tab:hide", "video:dispose", "resume"],
            rig.Calls);
        Assert.False(rig.Input.Held);
    }

    [Fact]
    public async Task APauseThatThrows_IsRefused_AndTheNextStartStillWorks()
    {
        using var rig = new Rig();
        rig.Previews.ThrowOnPause = true;
        rig.Start();
        var channel = rig.Open();

        Assert.Equal(ControlStatus.Refused, (await rig.NextStateAsync(channel)).Status);
        await rig.WaitForAsync(() => rig.Calls.Contains("resume"));
        Assert.Equal(["pause", "resume"], rig.Calls);
        Assert.Contains(rig.Log, line => line.Contains("ObjectDisposedException"));

        rig.Previews.ThrowOnPause = false;
        Assert.Equal(ControlStatus.Started, (await rig.NextStateAsync(rig.Open())).Status);
    }

    [Fact]
    public async Task AVideoThatHoldsItsThread_HoldsBackNeitherStartedNorInput()
    {
        using var rig = new Rig();
        rig.Video.Blocks = true;
        var channel = await rig.StartedAsync();

        await channel.FromConsole.Writer.WriteAsync(new PointerMove(1, 2), TestContext.Current.CancellationToken);
        await rig.WaitForAsync(() => rig.Calls.Contains("move:1,2"));

        channel.FromConsole.Writer.Complete();
        await rig.WaitForAsync(() => rig.Calls.Contains("resume"));
        Assert.False(rig.Input.Held);
    }

    [Fact]
    public async Task Start_OnALockedPc_Unlocks()
    {
        using var rig = new Rig(locked: true);

        await rig.StartedAsync();

        Assert.False(rig.Screen.IsLocked);
    }

    [Fact]
    public async Task Input_GoesToTheInjector()
    {
        using var rig = new Rig();
        var channel = await rig.StartedAsync();
        rig.Calls.Clear();

        foreach (var message in new ControlMessage[]
        {
            new PointerMove(1, 2), new PointerButton(MouseButton.Right, true), new PointerWheel(120, true), new KeyInput(65, 30, false, true),
        })
        {
            await channel.FromConsole.Writer.WriteAsync(message, TestContext.Current.CancellationToken);
        }

        await rig.WaitForAsync(() => rig.Calls.Count == 4);
        Assert.Equal(["move:1,2", "button:Right,True", "wheel:120,True", "key:65,30,False,True"], rig.Calls);
    }

    [Fact]
    public async Task ClipboardText_FromTheConsole_IsWritten()
    {
        using var rig = new Rig();
        var channel = await rig.StartedAsync();

        await channel.FromConsole.Writer.WriteAsync(new ClipboardText("hello"), TestContext.Current.CancellationToken);

        await rig.WaitForAsync(() => rig.Clipboard.Written.Count == 1);
        Assert.Equal(["hello"], rig.Clipboard.Written);
    }

    [Fact]
    public async Task ClipboardText_LongerThanTheCap_IsIgnored()
    {
        using var rig = new Rig();
        var channel = await rig.StartedAsync();

        await channel.FromConsole.Writer.WriteAsync(new ClipboardText(new string('a', ControlLimits.MaxClipboardChars + 1)), TestContext.Current.CancellationToken);
        await channel.FromConsole.Writer.WriteAsync(new ClipboardText("after"), TestContext.Current.CancellationToken);

        await rig.WaitForAsync(() => rig.Clipboard.Written.Count == 1);
        Assert.Equal(["after"], rig.Clipboard.Written);
    }

    [Fact]
    public async Task TextCopiedOnThisPc_IsSentToTheConsole()
    {
        using var rig = new Rig();
        var channel = await rig.StartedAsync();

        rig.Clipboard.Copy("copied");

        Assert.Equal("copied", Assert.IsType<ClipboardText>(await channel.ToConsole.Reader.ReadAsync(TestContext.Current.CancellationToken)).Text);
    }

    [Fact]
    public async Task RequestKeyframe_ReachesTheVideo()
    {
        using var rig = new Rig();
        var channel = await rig.StartedAsync();

        await channel.FromConsole.Writer.WriteAsync(new RequestKeyframe(), TestContext.Current.CancellationToken);

        await rig.WaitForAsync(() => rig.Video.Keyframes == 1);
    }

    [Fact]
    public async Task LeavingTheDesktop_IsReported_AndComingBack_Too()
    {
        using var rig = new Rig();
        var channel = await rig.StartedAsync();

        rig.OnDesktop(false);
        Assert.Equal(ControlStatus.ProtectedScreen, (await rig.NextStateAsync(channel)).Status);
        rig.OnDesktop(true);
        Assert.Equal(ControlStatus.Desktop, (await rig.NextStateAsync(channel)).Status);
    }

    [Theory]
    [InlineData("stream-closed")]
    [InlineData("connection-lost")]
    [InlineData("video-failed")]
    [InlineData("cancelled")]
    public async Task EveryEnding_ReleasesKeys_HidesTheTab_EndsControl_AndResumesPreviews(string ending)
    {
        using var rig = new Rig();
        var channel = await rig.StartedAsync();
        rig.Calls.Clear();

        switch (ending)
        {
            case "stream-closed": channel.FromConsole.Writer.Complete(); break;
            case "connection-lost": channel.FromConsole.Writer.Complete(new IOException("lost")); break;
            case "video-failed": rig.Video.Fail(new InvalidOperationException("encoder")); break;
            default: rig.Cancel(); break;
        }

        await rig.WaitForAsync(() => rig.Calls.Contains("resume"));
        Assert.Equal(["release-all", "clipboard:dispose", "release", "tab:hide", "video:dispose", "resume"], rig.Calls);
        Assert.False(rig.Input.Held);
        Assert.Contains(rig.Log, line => line.Contains("ended"));
    }

    [Fact]
    public async Task ASecondStart_AfterTheFirstEnded_Works()
    {
        using var rig = new Rig();
        var first = await rig.StartedAsync();
        first.FromConsole.Writer.Complete();
        await rig.WaitForAsync(() => rig.Calls.Contains("resume"));

        var second = rig.Open();

        Assert.Equal(ControlStatus.Started, (await rig.NextStateAsync(second)).Status);
    }

    [Fact]
    public async Task TheLog_NeverCarriesKeysOrClipboardText()
    {
        using var rig = new Rig();
        var channel = await rig.StartedAsync();

        await channel.FromConsole.Writer.WriteAsync(new KeyInput(65, 30, false, true), TestContext.Current.CancellationToken);
        await channel.FromConsole.Writer.WriteAsync(new ClipboardText("s3cret-text"), TestContext.Current.CancellationToken);
        rig.Clipboard.Copy("s3cret-copy");
        await rig.WaitForAsync(() => rig.Clipboard.Written.Count == 1);
        channel.FromConsole.Writer.Complete(new IOException("s3cret-failure"));
        await rig.WaitForAsync(() => rig.Calls.Contains("resume"));

        Assert.DoesNotContain(rig.Log, line => line.Contains("s3cret") || line.Contains("65"));
    }
}
