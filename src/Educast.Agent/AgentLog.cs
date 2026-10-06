using System.Text;

namespace Educast.Agent;

/// <summary>
/// The agent's log: plain text, one line per event, each with a local timestamp. It is capped: when the file has
/// reached <paramref name="maxBytes"/> it becomes <c>name.1</c> (replacing the one before) and a new file begins,
/// so the log never takes more than about twice that. A line that cannot be written is lost; logging never
/// throws.
/// </summary>
internal sealed class AgentLog(string path, long maxBytes = AgentLog.DefaultMaxBytes, TimeProvider? timeProvider = null)
{
    public const long DefaultMaxBytes = 1024 * 1024;

    private readonly Lock _gate = new();
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private long? _length;

    public string FilePath { get; } = path;

    /// <summary>Appends one line. Line breaks inside <paramref name="message"/> become spaces.</summary>
    public void Write(string message)
    {
        var line = $"{_time.GetLocalNow():yyyy-MM-dd HH:mm:ss.fff}  " +
                   $"{message.ReplaceLineEndings(" ")}{Environment.NewLine}";
        var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(line);

        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(FilePath))!);

                _length ??= File.Exists(FilePath) ? new FileInfo(FilePath).Length : 0;
                if (_length >= maxBytes)
                {
                    File.Move(FilePath, FilePath + ".1", overwrite: true);
                    _length = 0;
                }

                // Readable by someone tailing it while it is written.
                using var stream = new FileStream(
                    FilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                stream.Write(bytes);
                _length += bytes.Length;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Nowhere to say so.
                _length = null;
            }
        }
    }
}
