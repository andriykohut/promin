using System.Diagnostics;
using System.Globalization;

namespace Educast.Master.Core;

/// <summary>
/// The teacher PC's log: every <see cref="Trace"/> line, each with its time, in <c>console.log</c>;
/// the run before it is kept as <c>console.previous.log</c>. Readable while the console runs.
/// </summary>
public sealed class ConsoleLog : TextWriterTraceListener
{
    /// <summary>For <see cref="TryOpen"/>, and for a test that needs a writer that fails.</summary>
    public ConsoleLog(TextWriter writer)
        : base(writer)
    {
    }

    /// <summary>
    /// Moves the last run's log aside (replacing the one before it) and opens a new one in
    /// <paramref name="directory"/>, which is made if missing. Null, and nothing thrown, when that
    /// cannot be done (another console on this PC holds the log, or the disk refuses): the console
    /// then runs without a log.
    /// </summary>
    public static ConsoleLog? TryOpen(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "console.log");
            if (File.Exists(path))
            {
                File.Move(path, Path.Combine(directory, "console.previous.log"), overwrite: true);
            }

            return new ConsoleLog(new StreamWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read)));
        }
        catch (Exception)
        {
            return null;
        }
    }

    // Logging never throws: the broadcast writes here from its capture loop and its teardown, and a full disk
    // or a folder that has gone must not end a lesson. There is nowhere to say that a line was lost.

    public override void WriteLine(string? message)
    {
        try
        {
            base.WriteLine(message);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
        }
    }

    public override void Flush()
    {
        try
        {
            base.Flush();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
        }
    }

    public override void TraceEvent(TraceEventCache? eventCache, string source, TraceEventType eventType, int id, string? message) =>
        WriteLine($"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff}  {eventType}: {message}");

    public override void TraceEvent(
        TraceEventCache? eventCache, string source, TraceEventType eventType, int id, string? format, params object?[]? args) =>
        TraceEvent(
            eventCache,
            source,
            eventType,
            id,
            format is null || args is null ? format : string.Format(CultureInfo.InvariantCulture, format, args));
}
