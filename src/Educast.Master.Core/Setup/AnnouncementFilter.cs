using Educast.Master.Core.Rooms;

namespace Educast.Master.Core.Setup;

/// <summary>
/// Which discovery announcements are worth passing on to the screens. Anyone on the network can
/// send them at any rate, and each one passed on is work for the UI thread, so: an announcement
/// that says exactly what the last one passed on for that device said is dropped for a second,
/// and no more than <see cref="MaxDevices"/> devices are followed; others are ignored.
/// </summary>
/// <remarks>
/// A student PC announces itself every two seconds, so nothing real is dropped. The time is a
/// running millisecond count, not the clock, so a clock change cannot silence a PC.
/// </remarks>
public sealed class AnnouncementFilter
{
    /// <summary>The same bound Set up devices puts on its list.</summary>
    public const int MaxDevices = 200;

    private const long RepeatMilliseconds = 1000;

    private readonly Dictionary<string, (StudentPc Pc, long At)> _posted = new(StringComparer.Ordinal);

    /// <summary>Whether to pass this announcement on. One caller at a time.</summary>
    public bool ShouldPost(StudentPc pc, long nowMilliseconds)
    {
        ArgumentNullException.ThrowIfNull(pc);
        if (_posted.TryGetValue(pc.DeviceId, out var last))
        {
            if (last.Pc == pc && nowMilliseconds - last.At < RepeatMilliseconds)
            {
                return false;
            }
        }
        else if (_posted.Count >= MaxDevices)
        {
            return false;
        }

        _posted[pc.DeviceId] = (pc, nowMilliseconds);
        return true;
    }
}
