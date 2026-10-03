namespace Educast.Master.Core.Rooms;

/// <summary>A student PC as seen on the network.</summary>
public sealed record StudentPc(string DeviceId, string AnnouncedName, string Address, int Port);
