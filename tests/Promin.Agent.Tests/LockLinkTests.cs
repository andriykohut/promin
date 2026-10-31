using System.Threading.Channels;
using Promin.Protocol.Messages;
using Promin.Protocol.Pairing;

namespace Promin.Agent.Tests;

/// <summary>The lock stream as the agent handles it, over a fake channel and a <see cref="ScreenLock"/> on fakes.</summary>
public class LockLinkTests
{
    private sealed class FakeChannel : IControlChannel
    {
        public Channel<ControlMessage> FromConsole { get; } = Channel.CreateUnbounded<ControlMessage>();
        public Channel<ControlMessage> ToConsole { get; } = Channel.CreateUnbounded<ControlMessage>();

        public Task SendAsync(ControlMessage message, CancellationToken ct) => ToConsole.Writer.WriteAsync(message, ct).AsTask();

        public Task<ControlMessage> ReceiveAsync(CancellationToken ct) => FromConsole.Reader.ReadAsync(ct).AsTask();
    }

    private sealed class FakeConnection(FakeChannel channel) : IAgentConnection
    {
        public Task<IControlChannel> AcceptLockStreamAsync(CancellationToken ct) => Task.FromResult<IControlChannel>(channel);

        public Task<IIncomingSend> AcceptSendAsync(CancellationToken ct) => throw new NotSupportedException();

        public Task<ControlMessage> ReceiveControlAsync(CancellationToken ct) => throw new NotSupportedException();

        public ValueTask<byte[]> ReceiveVideoFrameAsync(CancellationToken ct) => throw new NotSupportedException();

        public Task SendControlAsync(ControlMessage message, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class Rig : IDisposable
    {
        private readonly TempDirectory _dir = new();
        private readonly CancellationTokenSource _stop = new(TimeSpan.FromSeconds(20));
        private readonly List<string> _calls = [];
        private readonly List<string> _log = [];

        public Rig(bool mayLock = true, bool locked = false, Action? onShow = null)
        {
            Screen = new ScreenLock(new LockFile(Path.Combine(_dir.Path, "lock"), _log.Add), new FakeScreen(_calls) { OnShow = onShow }, new FakeInput(_calls), _log.Add);
            if (locked)
            {
                Screen.Lock("x");
            }

            Running = new LockLink(Screen, mayLock, _log.Add).RunAsync(new FakeConnection(Channel), _stop.Token);
        }

        public FakeChannel Channel { get; } = new();
        public ScreenLock Screen { get; }
        public Task Running { get; }
        public IReadOnlyList<string> Calls => _calls;
        public IReadOnlyList<string> Log => _log;

        public async Task<LockState> NextStateAsync() =>
            Assert.IsType<LockState>(await Channel.ToConsole.Reader.ReadAsync(_stop.Token));

        public async Task<LockState> SendAsync(ControlMessage message)
        {
            await NextStateAsync();
            await Channel.FromConsole.Writer.WriteAsync(message);
            return await NextStateAsync();
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
    public async Task Connecting_ReportsTheStateAtOnce()
    {
        using var rig = new Rig(locked: true);

        Assert.True((await rig.NextStateAsync()).Locked);
    }

    [Fact]
    public async Task LockPc_LocksThePc_AndReports()
    {
        using var rig = new Rig();

        Assert.True((await rig.SendAsync(new LockPc("Back soon"))).Locked);
        Assert.True(rig.Screen.IsLocked);
        Assert.Contains("show:Back soon", rig.Calls);
    }

    [Fact]
    public async Task UnlockPc_UnlocksThePc_AndReports()
    {
        using var rig = new Rig(locked: true);

        Assert.False((await rig.SendAsync(new UnlockPc())).Locked);
        Assert.False(rig.Screen.IsLocked);
    }

    [Fact]
    public async Task LockPc_LongerThanTheLimit_IsRefused()
    {
        using var rig = new Rig();

        Assert.False((await rig.SendAsync(new LockPc(new string('a', LockLimits.MaxMessageLength + 1)))).Locked);
        Assert.False(rig.Screen.IsLocked);
        Assert.Contains(rig.Log, line => line.Contains("refused"));
    }

    [Fact]
    public async Task LockPc_FromTheConsoleOnThisPc_IsRefused()
    {
        using var rig = new Rig(mayLock: false);

        Assert.False((await rig.SendAsync(new LockPc("Back soon"))).Locked);
        Assert.False(rig.Screen.IsLocked);
    }

    [Fact]
    public async Task UnlockPc_FromTheConsoleOnThisPc_StillWorks()
    {
        using var rig = new Rig(mayLock: false, locked: true);

        Assert.False((await rig.SendAsync(new UnlockPc())).Locked);
        Assert.False(rig.Screen.IsLocked);
    }

    [Fact]
    public async Task TheStreamEnding_LeavesTheLockOn()
    {
        using var rig = new Rig(locked: true);
        await rig.NextStateAsync();

        rig.Channel.FromConsole.Writer.Complete(new IOException("the stream ended"));
        await rig.Running.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.True(rig.Screen.IsLocked);
    }

    [Fact]
    public async Task AnUnexpectedFailure_IsLogged_WithoutTheMessage()
    {
        using var rig = new Rig(onShow: () => throw new InvalidOperationException("window failed"));
        await rig.NextStateAsync();

        await rig.Channel.FromConsole.Writer.WriteAsync(new LockPc("secret words"), TestContext.Current.CancellationToken);
        await rig.Running.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Contains(rig.Log, line => line.Contains("InvalidOperationException"));
        Assert.DoesNotContain(rig.Log, line => line.Contains("secret words"));
    }
}
