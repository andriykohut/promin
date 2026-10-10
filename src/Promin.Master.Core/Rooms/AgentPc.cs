namespace Promin.Master.Core.Rooms;

/// <summary>An agent PC as seen on the network.</summary>
public sealed record AgentPc(string DeviceId, string AnnouncedName, string Address, int Port);
