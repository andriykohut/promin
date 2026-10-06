namespace Educast.Protocol.Pairing;

/// <summary>
/// How long a student PC waits before opening its next pairing window after a peer
/// failed to prove the room code. With one guess per window this holds a stranger to
/// a few hundred guesses a day against a million possible codes.
/// </summary>
public sealed class PairingBackoff
{
    public static readonly TimeSpan First = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan Longest = TimeSpan.FromMinutes(5);

    private int _failures;

    public TimeSpan RecordFailure()
    {
        _failures = Math.Min(_failures + 1, 30);
        var seconds = First.TotalSeconds * Math.Pow(2, _failures - 1);
        return TimeSpan.FromSeconds(Math.Min(seconds, Longest.TotalSeconds));
    }

    public void RecordSuccess() => _failures = 0;
}
