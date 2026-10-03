using Educast.Master.Core.Rooms;
using Educast.Master.Core.Setup;

namespace Educast.Master.Core.Tests.TestDoubles;

/// <summary>
/// A pairing client that answers from a table, records every call, and can hold one PC's
/// pairing open until the test lets it go.
/// </summary>
public sealed class FakePairingClient : IPairingClient
{
    private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>The result for a device id. A device not listed pairs.</summary>
    public Dictionary<string, PairResult> Results { get; } = new(StringComparer.Ordinal);

    public List<(string DeviceId, string Code)> Calls { get; } = [];

    public List<CancellationToken> Tokens { get; } = [];

    public List<string> Unpaired { get; } = [];

    /// <summary>The device whose pairing is held open until <see cref="Release"/>.</summary>
    public string? HoldDeviceId { get; set; }

    /// <summary>Completes when the held pairing has been asked for.</summary>
    public Task Reached => _reached.Task;

    public async Task<PairResult> PairAsync(StudentPc pc, string roomCode, CancellationToken ct)
    {
        Calls.Add((pc.DeviceId, roomCode));
        Tokens.Add(ct);

        if (pc.DeviceId == HoldDeviceId)
        {
            _reached.TrySetResult();
            await _gate.Task;
        }

        return Results.TryGetValue(pc.DeviceId, out var result) ? result : PairResult.Paired;
    }

    public void Unpair(string deviceId) => Unpaired.Add(deviceId);

    public void Release() => _gate.TrySetResult();
}
