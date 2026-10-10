using Promin.Protocol.Discovery;

namespace Promin.Master.Core.Rooms;

/// <summary>An agent PC that has been paired into this room.</summary>
public sealed record RoomMember(
    string DeviceId, string DisplayName, string LastAddress, int Port, DateOnly PairedOn, bool Selected)
{
    /// <summary>The longest name a PC can be given: the longest name it can announce.</summary>
    public const int MaxNameLength = DiscoveryAnnouncement.MaxDisplayNameLength;

    /// <summary>The name a rename asks for, trimmed and cut to <see cref="MaxNameLength"/> characters; null when nothing is left.</summary>
    public static string? CleanName(string? name)
    {
        name = (name ?? string.Empty).Trim();
        if (name.Length == 0)
        {
            return null;
        }

        return name.Length > MaxNameLength ? name[..MaxNameLength].TrimEnd() : name;
    }
}
