namespace Promin.Master.Core.Wall;

/// <summary>
/// Answers "is this PC dropping frames right now?" from a running total. A PC that
/// dropped one frame early in the lesson must not look unhealthy for the rest of it.
/// </summary>
public sealed class RecentDrops(TimeSpan window)
{
    private int _lastTotal;
    private DateTimeOffset? _lastDropAt;

    public int Total => _lastTotal;

    /// <summary>Record the running total as read at <paramref name="now"/>.</summary>
    public void Observe(int totalDropped, DateTimeOffset now)
    {
        if (totalDropped > _lastTotal)
        {
            _lastDropAt = now;
        }

        _lastTotal = totalDropped;
    }

    public bool IsDropping(DateTimeOffset now) => _lastDropAt is { } at && now - at < window;

    /// <summary>A new broadcast starts its count from zero.</summary>
    public void Reset()
    {
        _lastTotal = 0;
        _lastDropAt = null;
    }
}
