namespace Promin.Protocol.Tests.TestDoubles;

/// <summary>
/// A <see cref="TimeProvider"/> whose clock moves only when a test calls <see cref="Advance"/>.
/// Timers (including <see cref="PeriodicTimer"/> and a <see cref="CancellationTokenSource"/> built
/// with a delay and a time provider) fire from inside <see cref="Advance"/>, in due-time order,
/// with the clock set to their due time.
/// </summary>
public sealed class ManualTimeProvider : TimeProvider
{
    private readonly object _lock = new();
    private readonly List<ManualTimer> _timers = [];
    private long _nowTicks;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => Volatile.Read(ref _nowTicks);

    public override DateTimeOffset GetUtcNow() =>
        new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero) + TimeSpan.FromTicks(GetTimestamp());

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        timer.Change(dueTime, period);
        lock (_lock)
        {
            _timers.Add(timer);
        }

        return timer;
    }

    public void Advance(TimeSpan delta)
    {
        long target;
        lock (_lock)
        {
            target = _nowTicks + delta.Ticks;
        }

        while (true)
        {
            ManualTimer? next = null;
            lock (_lock)
            {
                foreach (var timer in _timers)
                {
                    if (timer.DueTicks is { } due && due <= target && (next is null || due < next.DueTicks))
                    {
                        next = timer;
                    }
                }

                if (next is null)
                {
                    _nowTicks = target;
                    return;
                }

                _nowTicks = Math.Max(_nowTicks, next.DueTicks!.Value);
                next.Reschedule(_nowTicks);
            }

            next.Fire();
        }
    }

    private void Remove(ManualTimer timer)
    {
        lock (_lock)
        {
            _timers.Remove(timer);
        }
    }

    private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        private long _periodTicks;

        // Guarded by owner._lock.
        public long? DueTicks { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner._lock)
            {
                DueTicks = dueTime == Timeout.InfiniteTimeSpan ? null : owner._nowTicks + dueTime.Ticks;
                _periodTicks = period == Timeout.InfiniteTimeSpan ? 0 : period.Ticks;
            }

            return true;
        }

        public void Reschedule(long now) => DueTicks = _periodTicks > 0 ? now + _periodTicks : null;

        public void Fire() => callback(state);

        public void Dispose()
        {
            lock (owner._lock)
            {
                DueTicks = null;
            }

            owner.Remove(this);
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
