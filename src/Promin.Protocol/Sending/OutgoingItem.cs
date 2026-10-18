namespace Promin.Protocol.Sending;

/// <summary>
/// One item of a send, as the console sends it. <paramref name="Path"/> is relative, with
/// <c>/</c> between folders. <paramref name="Open"/> opens a file's bytes when its turn comes,
/// and is null for a folder, which carries no bytes.
/// </summary>
public sealed record OutgoingItem(string Path, long Length, bool IsFolder, Func<Stream>? Open);
