using Promin.Protocol.Messages;
using Promin.Protocol.Sending;

namespace Promin.Agent;

/// <summary>
/// Where a send lands on an agent PC. Items are written into a staging folder outside the desktop, so a half-arrived
/// send is never seen, and <see cref="Finish"/> moves them into Desktop\Promin. A file replaces the old copy; a
/// folder merges into the old one file by file, so files the person saved in it stay.
/// </summary>
/// <param name="staging">%LocalAppData%\Promin\Incoming. Emptied at the start of each send and after it.</param>
/// <param name="promin">Desktop\Promin. Created when missing.</param>
/// <param name="freeBytes">Free bytes on the drive of a path.</param>
internal sealed class ReceivedFiles(string staging, string promin, Func<string, long> freeBytes) : IReceiveTarget
{
    private const int SharingViolation = unchecked((int)0x80070020);
    private const int LockViolation = unchecked((int)0x80070021);

    private readonly List<(string Name, bool IsFolder)> topLevel = [];

    /// <summary>The items sent at the top level, in arrival order, each once.</summary>
    public IReadOnlyList<(string Name, bool IsFolder)> TopLevel => topLevel;

    /// <summary>
    /// Deletes what an earlier send left in staging and creates it afresh. Refuses
    /// <see cref="SendOutcome.NotEnoughSpace"/> when the drive of staging or of the desktop has less than
    /// <paramref name="totalBytes"/> free.
    /// </summary>
    /// <exception cref="IOException">The leftover could not be deleted.</exception>
    public void Begin(long totalBytes)
    {
        if (Directory.Exists(staging))
        {
            Directory.Delete(staging, recursive: true);
        }

        Directory.CreateDirectory(staging);

        // On one drive both give the same answer; across two, the final move is a copy, so both need the room.
        if (freeBytes(staging) < totalBytes || freeBytes(Path.GetDirectoryName(promin)!) < totalBytes)
        {
            throw new SendRefusedException(SendOutcome.NotEnoughSpace);
        }
    }

    /// <exception cref="SendRefusedException"><paramref name="path"/> breaks <see cref="ReceivedPath.TryJoin"/>.</exception>
    public void AddFolder(string path) => Directory.CreateDirectory(Join(path, isFolder: true));

    /// <summary>Returns a new file in staging, creating its folders.</summary>
    /// <exception cref="SendRefusedException"><paramref name="path"/> breaks <see cref="ReceivedPath.TryJoin"/>.</exception>
    /// <exception cref="IOException">The send already carried an item at <paramref name="path"/>.</exception>
    public Stream AddFile(string path, long length)
    {
        var full = Join(path, isFolder: false);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        return new FileStream(full, FileMode.CreateNew, FileAccess.Write, FileShare.None);
    }

    /// <summary>
    /// Moves every item into Desktop\Promin, then deletes staging. Returns <see cref="SendOutcome.InUse"/> when an
    /// old copy is open and cannot be replaced, and <see cref="SendOutcome.Failed"/> when a file stands where a
    /// folder goes (Desktop\Promin itself included) or the reverse, or the disk fails otherwise; that existing item is
    /// left as it was. Either way the other items still move. Never throws for anything the disk does.
    /// </summary>
    public SendOutcome Finish()
    {
        var outcome = SendOutcome.Sent;
        Put(staging, promin, isFolder: true);
        Discard();
        return outcome;

        void Put(string from, string to, bool isFolder)
        {
            try
            {
                if (isFolder)
                {
                    // Throws, and leaves the file alone, when a file stands at to.
                    Directory.CreateDirectory(to);
                    foreach (var entry in new DirectoryInfo(from).GetFileSystemInfos())
                    {
                        Put(entry.FullName, Path.Combine(to, entry.Name), entry is DirectoryInfo);
                    }
                }
                else if (Directory.Exists(to))
                {
                    outcome = SendOutcome.Failed;
                }
                else
                {
                    File.Move(from, to, overwrite: true);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                if (!InUse(e, to))
                {
                    outcome = SendOutcome.Failed;
                }
                else if (outcome == SendOutcome.Sent)
                {
                    outcome = SendOutcome.InUse;
                }
            }
        }
    }

    /// <summary>Deletes staging, ignoring what cannot be deleted.</summary>
    public void Discard()
    {
        try
        {
            Directory.Delete(staging, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// True when <paramref name="e"/>, thrown putting an item at <paramref name="path"/>, is because the old copy
    /// there is open.
    /// </summary>
    private static bool InUse(Exception e, string path)
    {
        if (e.HResult is SharingViolation or LockViolation)
        {
            return true;
        }

        if (e is not UnauthorizedAccessException)
        {
            return false;
        }

        // A move onto an open file on the same drive fails as access denied, like one onto a read-only file, so
        // the file itself is asked: opening it with no sharing fails only while another program holds it.
        try
        {
            File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None).Dispose();
            return false;
        }
        catch (IOException probe) when (probe.HResult is SharingViolation or LockViolation)
        {
            return true;
        }
        catch (Exception probe) when (probe is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Joins <paramref name="path"/> under staging and notes its top-level item.</summary>
    private string Join(string path, bool isFolder)
    {
        if (!ReceivedPath.TryJoin(staging, path, out var full))
        {
            throw new SendRefusedException(SendOutcome.BadPath);
        }

        var slash = path.IndexOf('/');
        var name = slash < 0 ? path : path[..slash];
        if (!topLevel.Exists(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            topLevel.Add((name, isFolder || slash >= 0));
        }

        return full;
    }
}
