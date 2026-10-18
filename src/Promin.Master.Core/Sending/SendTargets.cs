using Promin.Master.Core.Rooms;

namespace Promin.Master.Core.Sending;

/// <summary>
/// A PC a send can go to. <paramref name="Member"/> carries the address the PC last announced from, which is where a
/// send dials it; <paramref name="Online"/> is whether it was seen recently.
/// </summary>
public sealed record SendTarget(RoomMember Member, string Name, bool Online);

/// <summary>The PCs the send window offers: the ones the wall has selected, and every PC in the room.</summary>
public sealed record SendTargets(IReadOnlyList<SendTarget> Selected, IReadOnlyList<SendTarget> All);
