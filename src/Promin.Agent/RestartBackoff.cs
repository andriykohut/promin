namespace Promin.Agent;

/// <summary>
/// How long the service waits before starting the agent again after it exited. The delay doubles from 2 s to a
/// 1-minute cap, so an agent that cannot start (a bad certificate, an unusable file) is retried steadily rather
/// than in a tight loop. <see cref="Reset"/> once the agent has stayed up long enough to count as healthy.
/// </summary>
internal sealed class RestartBackoff
{
    public static readonly TimeSpan First = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan Longest = TimeSpan.FromMinutes(1);

    private int _restarts;

    public TimeSpan NextDelay()
    {
        _restarts = Math.Min(_restarts + 1, 30);
        var seconds = First.TotalSeconds * Math.Pow(2, _restarts - 1);
        return TimeSpan.FromSeconds(Math.Min(seconds, Longest.TotalSeconds));
    }

    public void Reset() => _restarts = 0;
}
