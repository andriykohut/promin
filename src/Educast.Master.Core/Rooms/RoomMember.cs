namespace Educast.Master.Core.Rooms;

/// <summary>A student PC that has been paired into this room.</summary>
public sealed record RoomMember(
    string DeviceId, string DisplayName, string LastAddress, int Port, DateOnly PairedOn, bool Selected);
