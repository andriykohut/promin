namespace Promin.Master.Core.Sending;

/// <summary>What a tile's screen area shows of the room's send.</summary>
public enum TileSendMark
{
    None,

    /// <summary>A ring with <see cref="Wall.PcTile.SendPercent"/>.</summary>
    Sending,

    /// <summary>A tick: the PC has everything. It clears ten seconds after the send ends.</summary>
    Done,

    /// <summary>A red "!": the PC did not get the send. It stays until the next send.</summary>
    NotSent,
}

/// <summary>Why a PC did not get a send.</summary>
public enum NotSentReason
{
    /// <summary>Offline when the send started, or no connection to it came up within five seconds.</summary>
    Offline,

    /// <summary>The connection ended during the send.</summary>
    ConnectionLost,

    NotEnoughSpace,

    /// <summary>An older copy of an item is open on the PC and could not be replaced.</summary>
    InUse,

    /// <summary>Stop sending was pressed, or a file could not be read on this PC.</summary>
    Stopped,

    /// <summary>The PC refused the send or could not write it; its log has the details.</summary>
    Other,
}

/// <summary>Which send strip shows.</summary>
public enum SendStripKind
{
    Sending,

    /// <summary>The send ended, and at least one PC did not get it.</summary>
    SomeMissed,

    AllSent,

    /// <summary>A file could not be read on this PC, so the send stopped for every PC.</summary>
    SourceFailed,
}
