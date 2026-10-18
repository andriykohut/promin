using Promin.Protocol.Sending;

namespace Promin.Master.Core.Sending;

/// <summary>A file or folder the operator picked to send: <paramref name="Path"/> is its full path on this PC.</summary>
public sealed record PickedItem(string Path, bool IsFolder);

/// <summary>
/// What one send carries: the picked files and folders, with each folder walked into its tree, as the
/// <see cref="OutgoingItem"/>s the exchange writes. The same package can be sent to any number of PCs.
/// </summary>
/// <remarks>
/// Hidden and system files and folders (<c>desktop.ini</c>, <c>.git</c>) are left out of a picked folder, and links
/// inside it (symlinks and junctions) are neither sent nor followed: a junction to another drive must not pull that
/// drive into the send. Cloud placeholders (OneDrive files not downloaded) are not links and are sent. What the
/// operator picked by name is always sent, whatever its attributes.
/// </remarks>
public sealed class SendPackage
{
    // One folder's own entries; Folder recurses itself so that it can pass over links, which the enumerator would follow.
    private static readonly EnumerationOptions Level = new()
    {
        AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
        IgnoreInaccessible = false,
    };

    private SendPackage(IReadOnlyList<PickedItem> picked, IReadOnlyList<OutgoingItem> items)
    {
        Picked = picked;
        Items = items;
        TotalBytes = Bytes(items);
    }

    /// <summary>What the operator picked, in the order picked.</summary>
    public IReadOnlyList<PickedItem> Picked { get; }

    /// <summary>The files and folders to send. A folder comes before everything in it.</summary>
    public IReadOnlyList<OutgoingItem> Items { get; }

    /// <summary>The bytes of every file in <see cref="Items"/>.</summary>
    public long TotalBytes { get; }

    /// <summary>Walks the picked folders now. Throws <see cref="IOException"/> or <see cref="UnauthorizedAccessException"/> when one cannot be read.</summary>
    public static SendPackage From(IReadOnlyList<PickedItem> picked)
    {
        ArgumentNullException.ThrowIfNull(picked);
        return new SendPackage([.. picked], [.. picked.SelectMany(Expand)]);
    }

    /// <summary>Bytes in a picked file, or in every file under a picked folder, by the same rules as <see cref="From"/>.</summary>
    public static long SizeOf(PickedItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return Bytes(Expand(item));
    }

    private static long Bytes(IEnumerable<OutgoingItem> items) => items.Where(i => !i.IsFolder).Sum(i => i.Length);

    private static IEnumerable<OutgoingItem> Expand(PickedItem picked)
    {
        if (!picked.IsFolder)
        {
            var file = new FileInfo(picked.Path);
            return [FileItem(file, file.Name)];
        }

        var root = new DirectoryInfo(picked.Path);
        return Folder(root, root.Name);
    }

    // A folder comes before everything in it. Links (symlinks and junctions) are passed over, file or folder; a
    // cloud placeholder also carries the reparse-point attribute but has no link target, and is sent like any file.
    private static IEnumerable<OutgoingItem> Folder(DirectoryInfo folder, string path)
    {
        yield return new OutgoingItem(path, 0, IsFolder: true, Open: null);
        foreach (var entry in folder.EnumerateFileSystemInfos("*", Level))
        {
            if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint) && entry.LinkTarget is not null)
            {
                continue;
            }

            var entryPath = path + "/" + entry.Name;
            if (entry is DirectoryInfo directory)
            {
                foreach (var item in Folder(directory, entryPath))
                {
                    yield return item;
                }
            }
            else
            {
                yield return FileItem((FileInfo)entry, entryPath);
            }
        }
    }

    private static OutgoingItem FileItem(FileInfo file, string path) =>
        new(path, file.Length, IsFolder: false, () => File.OpenRead(file.FullName));
}
