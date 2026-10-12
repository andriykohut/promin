using Promin.Agent;

namespace Promin.Agent.Tests;

public class AgentSupervisorTests
{
    private sealed class FakeProcess : IAgentProcess
    {
        private readonly TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Killed { get; private set; }
        public void ExitNow() => _exited.TrySetResult();
        public void Kill() { Killed = true; _exited.TrySetResult(); }
        public Task WaitForExitAsync(CancellationToken ct) => _exited.Task.WaitAsync(ct);
        public void Dispose() { }
    }

    private sealed class FakeLauncher : ISessionLauncher
    {
        public List<uint> Launched { get; } = [];
        public List<FakeProcess> Processes { get; } = [];
        public Func<uint, bool> Throw { get; set; } = _ => false;
        public IAgentProcess LaunchInSession(uint sessionId)
        {
            if (Throw(sessionId)) throw new InvalidOperationException("cannot launch");
            Launched.Add(sessionId);
            var p = new FakeProcess();
            Processes.Add(p);
            return p;
        }
    }

    private sealed class FakeSessions : IConsoleSessions
    {
        private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public uint? ActiveSession { get; private set; }
        public void Set(uint? session) { ActiveSession = session; var old = _changed; _changed = new(TaskCreationOptions.RunContinuationsAsynchronously); old.TrySetResult(); }
        public Task WaitForChangeAsync(CancellationToken ct) => _changed.Task.WaitAsync(ct);
    }

    // Drains the supervisor's continuations, which hop through the thread pool (RunContinuationsAsynchronously,
    // CancellationTokenSource.CancelAsync). The short real delay settles those cross-thread hops; it uses the real
    // clock and is independent of the SUT's injected manual clock. Yields on either side drain same-context work.
    private static async Task PumpAsync()
    {
        for (var i = 0; i < 10; i++) await Task.Yield();
        await Task.Delay(25);
        for (var i = 0; i < 10; i++) await Task.Yield();
    }

    [Fact]
    public async Task LaunchesTheAgentInTheSessionOnTheConsole()
    {
        var launcher = new FakeLauncher();
        var sessions = new FakeSessions();
        sessions.Set(1);
        using var stop = new CancellationTokenSource();
        var run = new AgentSupervisor(launcher, sessions, new RestartBackoff(), new ManualTimeProvider(), _ => { }).RunAsync(stop.Token);

        await PumpAsync();

        Assert.Equal([1u], launcher.Launched);
        await stop.CancelAsync();
        await run;
    }

    [Fact]
    public async Task IdlesWhenNobodyIsSignedIn_ThenLaunchesOnSignIn()
    {
        var launcher = new FakeLauncher();
        var sessions = new FakeSessions(); // ActiveSession null
        using var stop = new CancellationTokenSource();
        var run = new AgentSupervisor(launcher, sessions, new RestartBackoff(), new ManualTimeProvider(), _ => { }).RunAsync(stop.Token);
        await PumpAsync();
        Assert.Empty(launcher.Launched);

        sessions.Set(2);
        await PumpAsync();

        Assert.Equal([2u], launcher.Launched);
        await stop.CancelAsync();
        await run;
    }

    [Fact]
    public async Task EndsTheAgentWhenTheSessionEnds_AndDoesNotRelaunchUntilSomeoneSignsIn()
    {
        var launcher = new FakeLauncher();
        var sessions = new FakeSessions();
        sessions.Set(1);
        using var stop = new CancellationTokenSource();
        var run = new AgentSupervisor(launcher, sessions, new RestartBackoff(), new ManualTimeProvider(), _ => { }).RunAsync(stop.Token);
        await PumpAsync();

        sessions.Set(null); // signed out
        await PumpAsync();

        Assert.True(launcher.Processes[0].Killed);
        Assert.Single(launcher.Launched);
        await stop.CancelAsync();
        await run;
    }

    [Fact]
    public async Task FollowsAUserSwitch()
    {
        var launcher = new FakeLauncher();
        var sessions = new FakeSessions();
        sessions.Set(1);
        using var stop = new CancellationTokenSource();
        var run = new AgentSupervisor(launcher, sessions, new RestartBackoff(), new ManualTimeProvider(), _ => { }).RunAsync(stop.Token);
        await PumpAsync();

        sessions.Set(2); // switched to another signed-in user
        await PumpAsync();

        Assert.True(launcher.Processes[0].Killed);
        Assert.Equal([1u, 2u], launcher.Launched);
        await stop.CancelAsync();
        await run;
    }

    [Fact]
    public async Task RestartsTheAgentWithAGrowingDelayWhenItKeepsExiting()
    {
        var launcher = new FakeLauncher();
        var sessions = new FakeSessions();
        sessions.Set(1);
        var clock = new ManualTimeProvider();
        using var stop = new CancellationTokenSource();
        var run = new AgentSupervisor(launcher, sessions, new RestartBackoff(), clock, _ => { }).RunAsync(stop.Token);
        await PumpAsync();

        launcher.Processes[0].ExitNow();          // exits at once
        await PumpAsync();
        Assert.Single(launcher.Launched);         // still waiting out the backoff
        clock.Advance(TimeSpan.FromSeconds(2));   // first delay
        await PumpAsync();
        Assert.Equal(2, launcher.Launched.Count);

        launcher.Processes[1].ExitNow();
        await PumpAsync();
        clock.Advance(TimeSpan.FromSeconds(2));   // not enough: second delay is 4 s
        await PumpAsync();
        Assert.Equal(2, launcher.Launched.Count);
        clock.Advance(TimeSpan.FromSeconds(2));
        await PumpAsync();
        Assert.Equal(3, launcher.Launched.Count);

        await stop.CancelAsync();
        await run;
    }

    [Fact]
    public async Task AFlipAwayAndBackToTheSameSession_EndsTheFirstAgent_AndLaunchesExactlyOneReplacement()
    {
        var launcher = new FakeLauncher();
        var sessions = new FakeSessions();
        sessions.Set(1);
        using var stop = new CancellationTokenSource();
        var run = new AgentSupervisor(launcher, sessions, new RestartBackoff(), new ManualTimeProvider(), _ => { }).RunAsync(stop.Token);
        await PumpAsync();

        // The console leaves session 1 and returns to it while the first agent is still alive (1 -> 2 -> 1).
        sessions.Set(2);
        sessions.Set(1);
        await PumpAsync();

        Assert.True(launcher.Processes[0].Killed);     // the first agent was ended, not leaked
        Assert.Equal([1u, 1u], launcher.Launched);     // exactly one replacement, in session 1
        Assert.False(launcher.Processes[1].Killed);    // the replacement is still running
        await stop.CancelAsync();
        await run;
    }

    [Fact]
    public async Task StopsTheRunningAgentWhenToldToStop()
    {
        var launcher = new FakeLauncher();
        var sessions = new FakeSessions();
        sessions.Set(1);
        using var stop = new CancellationTokenSource();
        var run = new AgentSupervisor(launcher, sessions, new RestartBackoff(), new ManualTimeProvider(), _ => { }).RunAsync(stop.Token);
        await PumpAsync();

        await stop.CancelAsync();
        await run;

        Assert.True(launcher.Processes[0].Killed);
    }
}
