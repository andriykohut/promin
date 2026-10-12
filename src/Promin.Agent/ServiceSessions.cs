namespace Promin.Agent;

/// <summary>
/// Which session is on the console, kept current from the service's session-change notifications. <see cref="Apply"/>
/// is the whole rule and is unit-tested; the notifications themselves come from the service host (hand-tested).
/// </summary>
internal sealed class ServiceSessions(Func<uint?> initialConsoleSession) : IConsoleSessions
{
    private readonly Lock _gate = new();
    private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private uint? _active = initialConsoleSession();

    public uint? ActiveSession { get { lock (_gate) { return _active; } } }

    public Task WaitForChangeAsync(CancellationToken ct)
    {
        Task task;
        lock (_gate)
        {
            task = _changed.Task;
        }

        return task.WaitAsync(ct);
    }

    public void Apply(ServiceSessionChange change)
    {
        lock (_gate)
        {
            var next = change.Event switch
            {
                ServiceSessionEvent.SignedInOrConnected => (uint)change.SessionId,
                ServiceSessionEvent.SignedOutOrDisconnected when _active == (uint)change.SessionId => (uint?)null,
                _ => _active,
            };

            if (next == _active)
            {
                return;
            }

            _active = next;
            // Swap in a fresh completion source only on a real change, so a wait armed before the reading of
            // ActiveSession still completes (it holds the one swapped out here). This is the no-lost-change
            // contract documented on IConsoleSessions.WaitForChangeAsync.
            var old = _changed;
            _changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            old.TrySetResult();
        }
    }
}
