using Educast.Master.Core.Rooms;

namespace Educast.Master.Core.Setup;

/// <summary>The transport, as the setup screen needs it: pair one PC, forget one, or ask about one.</summary>
public interface IPairingClient
{
    /// <summary>
    /// Pairs with <paramref name="pc"/> using <paramref name="roomCode"/>, which is exactly six
    /// ASCII digits. Returns a result for every failure the PC or the network can cause; throws
    /// only for the caller's own cancellation and for programming errors.
    /// </summary>
    Task<PairResult> PairAsync(StudentPc pc, string roomCode, CancellationToken ct);

    /// <summary>Forget a PC's certificate on this machine.</summary>
    void Unpair(string deviceId);

    /// <summary>True when this machine holds a certificate pin for the PC, so it can be dialled.</summary>
    bool IsTrusted(string deviceId);
}
