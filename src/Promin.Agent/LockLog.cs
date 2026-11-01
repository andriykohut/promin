namespace Promin.Agent;

/// <summary>
/// The log of the lock and of the file, window and input block it is made of. A restore that fails is tried again every
/// few seconds, so during a restore a line that has already been written is dropped, until <see cref="Forget"/>. Every
/// other line is written: a person asked for something, or a lock is up and each line is news. Safe from any thread.
/// </summary>
internal sealed class LockLog(Action<string> log)
{
    private readonly Lock _gate = new();
    private readonly HashSet<string> _written = [];
    private volatile bool _quiet;

    public void Write(string line)
    {
        lock (_gate)
        {
            if (!_written.Add(line) && _quiet)
            {
                return;
            }
        }

        log(line);
    }

    /// <summary>Runs <paramref name="attempt"/>, during which a line that has already been written is dropped.</summary>
    public void Quietly(Action attempt)
    {
        _quiet = true;
        try
        {
            attempt();
        }
        finally
        {
            _quiet = false;
        }
    }

    /// <summary>The failures so far are over: any of them that comes back is written again.</summary>
    public void Forget()
    {
        lock (_gate)
        {
            _written.Clear();
        }
    }
}
