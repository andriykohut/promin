using System.Text;
using Educast.Protocol.Pairing;

namespace Educast.Protocol.Transport;

/// <summary>
/// When a student PC opens its pairing window, for whom, and how long it waits after a window that went wrong.
/// </summary>
/// <remarks>
/// <para>
/// A student PC is the only thing between a stranger on the LAN and a guess at the room code, so the rules are
/// about guesses:
/// </para>
/// <list type="number">
/// <item>With no room code set no window is opened, and that is said once.</item>
/// <item>
/// With no teacher PC pinned, windows are open to <see cref="PairingAdmission.Anyone"/>, one straight after
/// another: a window that ends with nobody dialling costs nothing and is followed by the next at once.
/// </item>
/// <item>
/// With a teacher PC pinned, windows admit <see cref="PairingAdmission.PinnedPeersOnly"/>, so knowing the code
/// is of no use against a PC that has already been paired, and the pinned teacher PC can pair again.
/// </item>
/// <item>
/// Any window that let a peer in and ended without that peer pairing costs the next step of
/// <see cref="PairingBackoff"/> before the next window opens. That is wider than "the peer failed to prove the
/// code": a peer can run the first rounds of the exchange, learn from what this PC sends back whether its guess
/// was right, and walk away, and that has to cost as much as a wrong proof. A pairing starts the backoff again.
/// </item>
/// </list>
/// <para>
/// The room code is read each time a window is about to open, so setting it does not need a restart. The pins
/// are the store passed in; the loop saves it after a pairing.
/// </para>
/// </remarks>
public sealed class AgentPairingLoop
{
    /// <summary>What is logged, once, while no room code is set.</summary>
    public const string NoRoomCodeMessage =
        "No room code is set. Run \"Educast.Agent.exe --set-room-code <6 digits>\".";

    /// <summary>How often the room code is looked for while there is none.</summary>
    public static readonly TimeSpan NoRoomCodeRecheck = TimeSpan.FromSeconds(10);

    private readonly PeerCertificateStore _trustedMasters;
    private readonly Func<string?> _loadRoomCode;
    private readonly Func<byte[], PairingAdmission, CancellationToken, Task<PairingOutcome>> _openWindow;
    private readonly Action _saveTrustedMasters;
    private readonly Action<string> _log;
    private readonly PairingBackoff _backoff;
    private readonly TimeProvider _timeProvider;

    /// <param name="trustedMasters">The pins the listener judges by; empty means no teacher PC is paired.</param>
    /// <param name="loadRoomCode">The six digits, or null when none is set.</param>
    /// <param name="openWindow">
    /// Opens one pairing window for the given code and admission and returns how it ended (in the agent,
    /// <see cref="QuicAgentListener.AcceptPairingAsync"/>).
    /// </param>
    /// <param name="saveTrustedMasters">Writes the pins to disk after a pairing.</param>
    /// <param name="log">One line per event.</param>
    public AgentPairingLoop(
        PeerCertificateStore trustedMasters,
        Func<string?> loadRoomCode,
        Func<byte[], PairingAdmission, CancellationToken, Task<PairingOutcome>> openWindow,
        Action saveTrustedMasters,
        Action<string> log,
        PairingBackoff backoff,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(trustedMasters);
        ArgumentNullException.ThrowIfNull(loadRoomCode);
        ArgumentNullException.ThrowIfNull(openWindow);
        ArgumentNullException.ThrowIfNull(saveTrustedMasters);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(backoff);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _trustedMasters = trustedMasters;
        _loadRoomCode = loadRoomCode;
        _openWindow = openWindow;
        _saveTrustedMasters = saveTrustedMasters;
        _log = log;
        _backoff = backoff;
        _timeProvider = timeProvider;
    }

    /// <summary>Runs until <paramref name="ct"/> is cancelled, and then returns normally.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        var saidNoRoomCode = false;
        PairingAdmission? saidAdmission = null;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var roomCode = _loadRoomCode();
                if (roomCode is null)
                {
                    if (!saidNoRoomCode)
                    {
                        _log(NoRoomCodeMessage);
                        saidNoRoomCode = true;
                    }

                    await Task.Delay(NoRoomCodeRecheck, _timeProvider, ct).ConfigureAwait(false);
                    continue;
                }

                saidNoRoomCode = false;

                var admission = _trustedMasters.Count == 0 ? PairingAdmission.Anyone : PairingAdmission.PinnedPeersOnly;
                if (saidAdmission != admission)
                {
                    saidAdmission = admission;
                    _log(admission == PairingAdmission.Anyone
                        ? "Pairing is open: no teacher PC is paired yet, so any teacher PC that knows the room code may pair."
                        : "Pairing is open to the paired teacher PC only.");
                }

                TimeSpan? wait = null;
                try
                {
                    var outcome = await _openWindow(Encoding.ASCII.GetBytes(roomCode), admission, ct).ConfigureAwait(false);
                    wait = Judge(outcome);
                }
                catch (Exception failure) when (failure is not OperationCanceledException)
                {
                    wait = _backoff.RecordFailure();
                    _log($"A pairing window failed unexpectedly ({failure.GetType().Name}: {failure.Message}). " +
                         $"The next one opens in {Seconds(wait.Value)} s.");
                }

                if (wait is { } delay)
                {
                    await Task.Delay(delay, _timeProvider, ct).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Stopping is the only way out of this loop.
        }
    }

    /// <summary>Records what the window's end means for the backoff; returns how long to wait before the next one, if at all.</summary>
    private TimeSpan? Judge(PairingOutcome outcome)
    {
        if (outcome.Paired)
        {
            _backoff.RecordSuccess();
            _log($"Paired with teacher PC {outcome.DeviceId}.");
            try
            {
                _saveTrustedMasters();
            }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
            {
                _log($"The pairing with {outcome.DeviceId} could not be saved ({failure.Message}). It holds until " +
                     "this program stops, and then the teacher PC has to be paired again.");
            }

            return null;
        }

        if (outcome.Failure == PairingFailure.NoMasterArrived)
        {
            return null;
        }

        var wait = _backoff.RecordFailure();
        _log($"A teacher PC was let in and did not pair ({outcome.Failure}): {outcome.FailureReason} " +
             $"The next pairing window opens in {Seconds(wait)} s.");
        return wait;
    }

    private static string Seconds(TimeSpan span) => span.TotalSeconds.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
}
