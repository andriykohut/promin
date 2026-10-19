namespace Promin.Protocol.Messages;

/// <summary>How a file send ended on the receiving PC, carried by <see cref="SendDone"/>.</summary>
public enum SendOutcome
{
    /// <summary>Every item is in place.</summary>
    Sent,

    /// <summary>The disk has less free space than the files need; nothing was written.</summary>
    NotEnoughSpace,

    /// <summary>An older copy of an item is open on that PC, so it could not be replaced. The other items still arrived.</summary>
    InUse,

    /// <summary>An item's path would land outside the receiving folder, or is not a valid name.</summary>
    BadPath,

    /// <summary>That PC is already receiving another send.</summary>
    Busy,

    /// <summary>Anything else that went wrong while receiving.</summary>
    Failed,
}
