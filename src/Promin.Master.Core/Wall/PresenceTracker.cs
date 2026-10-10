namespace Promin.Master.Core.Wall;

/// <summary>
/// Which student PCs are on the network right now: a PC is online while it was seen less than
/// <paramref name="timeout"/> ago (at exactly the timeout it is offline).
/// </summary>
public sealed class PresenceTracker(TimeSpan timeout)
{
    private readonly Dictionary<string, DateTimeOffset> _lastSeen = new(StringComparer.Ordinal);

    public void Seen(string deviceId, DateTimeOffset now) => _lastSeen[deviceId] = now;

    public bool IsOnline(string deviceId, DateTimeOffset now) =>
        _lastSeen.TryGetValue(deviceId, out var lastSeen) && now - lastSeen < timeout;
}
