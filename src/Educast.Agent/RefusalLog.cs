namespace Educast.Agent;

/// <summary>
/// Says in the log that connections failed to be accepted, without letting whoever is dialling fill the log (and
/// push everything else out of it): the first at once, then at most one line per interval that says how many
/// there were since. Most are a peer turned away during the TLS handshake, but the listener reports every failure
/// to accept a connection through the same callback, so the wording does not claim more than that.
/// </summary>
internal sealed class RefusalLog(Action<string> log, TimeSpan interval, TimeProvider? timeProvider = null)
{
    private readonly Lock _gate = new();
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private long? _lastLine;
    private int _unreported;

    /// <summary>One connection was refused. Safe from any thread.</summary>
    public void Record(Exception refusal)
    {
        string? line = null;
        lock (_gate)
        {
            var now = _time.GetTimestamp();
            if (_lastLine is null)
            {
                line = $"A connection could not be accepted (usually a peer turned away during the TLS handshake): {refusal.Message}";
                _lastLine = now;
            }
            else if (_time.GetElapsedTime(_lastLine.Value, now) >= interval)
            {
                var count = _unreported + 1;
                line = $"{count} connections could not be accepted since the last report (usually peers turned away during the TLS handshake); the latest: {refusal.Message}";
                _lastLine = now;
                _unreported = 0;
            }
            else
            {
                _unreported++;
            }
        }

        if (line is not null)
        {
            log(line);
        }
    }
}
