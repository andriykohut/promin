namespace Promin.Master.Core.Wall;

public enum PcTileState
{
    Ready,
    Offline,
    Receiving,
    DroppingFrames,
    Reconnecting,
    Locked,
    Controlled,

    /// <summary>Being updated, from the click until it is back on the new version or counted as not updated.</summary>
    Updating,
}
