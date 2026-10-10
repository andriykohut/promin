using Promin.Master.Core.Rooms;
using Promin.Master.Core.Setup;

namespace Promin.Master.Core.Tests.TestDoubles;

/// <summary>
/// A pairing client that answers from a table, records every call, can hold one PC's pairing
/// open until the test lets it go, and can fail the way the real one can: throw, or honour
/// its cancellation token.
/// </summary>
public sealed class FakePairingClient : IPairingClient
{
    private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>The result for a device id. A device not listed pairs.</summary>
    public Dictionary<string, PairResult> Results { get; } = new(StringComparer.Ordinal);

    /// <summary>Devices whose pairing throws this instead of answering.</summary>
    public Dictionary<string, Exception> Throws { get; } = new(StringComparer.Ordinal);

    public List<(string DeviceId, string Code)> Calls { get; } = [];

    public List<CancellationToken> Tokens { get; } = [];

    public List<string> Unpaired { get; } = [];

    /// <summary>Thrown by <see cref="Unpair"/> when set.</summary>
    public Exception? UnpairThrows { get; set; }

    /// <summary>Which devices this console holds a pin for. Everything, unless a test says otherwise.</summary>
    public Func<string, bool> Trusted { get; set; } = _ => true;

    /// <summary>The device whose pairing is held open until <see cref="Release"/>.</summary>
    public string? HoldDeviceId { get; set; }

    /// <summary>When true, a held pairing throws <see cref="OperationCanceledException"/> if its token is cancelled.</summary>
    public bool HonoursCancellation { get; set; }

    /// <summary>Completes when the held pairing has been asked for.</summary>
    public Task Reached => _reached.Task;

    public async Task<PairResult> PairAsync(StudentPc pc, string roomCode, CancellationToken ct)
    {
        Calls.Add((pc.DeviceId, roomCode));
        Tokens.Add(ct);

        if (Throws.TryGetValue(pc.DeviceId, out var exception))
        {
            throw exception;
        }

        if (pc.DeviceId == HoldDeviceId)
        {
            _reached.TrySetResult();
            if (HonoursCancellation)
            {
                await _gate.Task.WaitAsync(ct);
            }
            else
            {
                await _gate.Task;
            }
        }

        return Results.TryGetValue(pc.DeviceId, out var result) ? result : PairResult.Paired;
    }

    public void Unpair(string deviceId)
    {
        if (UnpairThrows is not null)
        {
            throw UnpairThrows;
        }

        Unpaired.Add(deviceId);
    }

    public bool IsTrusted(string deviceId) => Trusted(deviceId);

    public void Release() => _gate.TrySetResult();
}
