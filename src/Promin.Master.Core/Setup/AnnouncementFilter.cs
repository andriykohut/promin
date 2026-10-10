using Promin.Master.Core.Rooms;

namespace Promin.Master.Core.Setup;

/// <summary>
/// Which discovery announcements are worth passing on to the screens. Anyone on the network can
/// send them at any rate, and each one passed on is work for the UI thread. Three rules:
/// <list type="number">
/// <item>Whatever it says, a device is passed on at most once in half a second.</item>
/// <item>A device this console has paired is always followed, and is not counted.</item>
/// <item>
/// At most <see cref="MaxDevices"/> other devices are followed. When that many are and a new one
/// announces itself, those not heard from for ten seconds are forgotten first (looked for at most
/// once a second); the new one is ignored only if the table is still full.
/// </item>
/// </list>
/// </summary>
/// <remarks>
/// An agent PC announces itself every two seconds, so nothing real is dropped, and a flood of
/// forged devices can hide an unpaired PC only for as long as it lasts, and a paired one never.
/// The time is a running millisecond count, not the clock, so a clock change cannot silence a PC.
/// </remarks>
/// <param name="isPinned">Whether this console has paired the device with that id. Called on the caller's thread.</param>
public sealed class AnnouncementFilter(Func<string, bool> isPinned)
{
    /// <summary>The same bound Set up devices puts on its list.</summary>
    public const int MaxDevices = 200;

    private const long PostIntervalMilliseconds = 500;
    private const long ForgetAfterMilliseconds = 10_000;
    private const long SweepIntervalMilliseconds = 1000;

    // When each device was last passed on. Paired ones apart, because only the others are bounded.
    private readonly Dictionary<string, long> _paired = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _others = new(StringComparer.Ordinal);
    private long? _sweptAt;

    /// <summary>How many devices this console has not paired are followed now; never more than <see cref="MaxDevices"/>.</summary>
    public int UnpinnedTracked => _others.Count;

    /// <summary>Whether to pass this announcement on. One caller at a time.</summary>
    public bool ShouldPost(AgentPc pc, long nowMilliseconds)
    {
        ArgumentNullException.ThrowIfNull(pc);
        var paired = isPinned(pc.DeviceId);
        var postedAt = paired ? _paired : _others;
        if (postedAt.TryGetValue(pc.DeviceId, out var last))
        {
            if (nowMilliseconds - last < PostIntervalMilliseconds)
            {
                return false;
            }
        }
        else if (!paired && _others.Count >= MaxDevices)
        {
            // Looking through the table costs the same whoever asks; a flood of new ids gets one look a second.
            if (_sweptAt is { } swept && nowMilliseconds - swept < SweepIntervalMilliseconds)
            {
                return false;
            }

            _sweptAt = nowMilliseconds;
            foreach (var quiet in _others.Where(o => nowMilliseconds - o.Value >= ForgetAfterMilliseconds).Select(o => o.Key).ToList())
            {
                _others.Remove(quiet);
            }

            if (_others.Count >= MaxDevices)
            {
                return false;
            }
        }

        postedAt[pc.DeviceId] = nowMilliseconds;
        return true;
    }
}
