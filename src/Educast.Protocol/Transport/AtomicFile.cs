namespace Educast.Protocol.Transport;

/// <summary>
/// The one way this project saves a small file that a device's trust or identity depends on:
/// a complete temporary file beside the destination, flushed to the disk, then swapped in.
/// A crash or a full disk leaves the previous file intact rather than a truncated one, and a
/// power cut straight after the swap cannot leave a file whose content never reached the disk.
/// </summary>
internal static class AtomicFile
{
    /// <summary>Writes <paramref name="content"/> to <paramref name="path"/> atomically, creating the directory if needed.</summary>
    public static void WriteAllBytes(string path, byte[] content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(content);

        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporaryPath = $"{fullPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(content);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(fullPath))
            {
                File.Replace(temporaryPath, fullPath, destinationBackupFileName: null);
            }
            else
            {
                File.Move(temporaryPath, fullPath);
            }
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}
