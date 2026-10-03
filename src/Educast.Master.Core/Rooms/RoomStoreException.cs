namespace Educast.Master.Core.Rooms;

/// <summary>
/// The room file could not be read or written: locked by another process, access denied, a
/// full disk. Not a malformed file — that is set aside and read as empty.
/// </summary>
public sealed class RoomStoreException : Exception
{
    public RoomStoreException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
