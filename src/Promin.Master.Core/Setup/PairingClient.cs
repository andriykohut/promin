using System.Text;
using System.Text.Json;
using Promin.Master.Core.Rooms;
using Promin.Protocol.Pairing;
using Promin.Protocol.Transport;

namespace Promin.Master.Core.Setup;

/// <summary>
/// The console's side of pairing around the transport: what bytes the room code becomes, how
/// the transport's verdict reads on the screen, and keeping the trust file on disk current.
/// </summary>
/// <remarks>
/// The transport (<c>QuicMasterConnector.PairAsync</c>) pins an agent PC's certificate in the
/// <see cref="PeerCertificateStore"/> on success but persists nothing; the store has
/// <see cref="PeerCertificateStore.Save"/> and <see cref="PeerCertificateStore.Load"/> and
/// leaves the when to its owner. That owner is this class: it saves after every successful
/// pairing and after every unpin, and <see cref="LoadTrust"/> loads at start. The actual
/// dialling is a delegate so that the WinUI adapter can supply the QUIC connector and this
/// class can be tested without a network.
/// </remarks>
public sealed class PairingClient : IPairingClient
{
    /// <summary>
    /// Dials <paramref name="pc"/> and runs the pairing handshake with <paramref name="presharedKey"/>.
    /// Returns <see cref="PairingFailure.None"/> when paired. Throws only for cancellation and
    /// programming errors.
    /// </summary>
    public delegate Task<PairingFailure> PairDelegate(AgentPc pc, byte[] presharedKey, CancellationToken ct);

    private readonly PairDelegate _pair;
    private readonly PeerCertificateStore _trustedAgents;
    private readonly string _trustPath;

    public PairingClient(PairDelegate pair, PeerCertificateStore trustedAgents, string trustPath)
    {
        ArgumentNullException.ThrowIfNull(pair);
        ArgumentNullException.ThrowIfNull(trustedAgents);
        ArgumentException.ThrowIfNullOrWhiteSpace(trustPath);

        _pair = pair;
        _trustedAgents = trustedAgents;
        _trustPath = trustPath;
    }

    /// <summary>
    /// Fills <paramref name="trustedAgents"/> from the file at <paramref name="path"/>. A missing
    /// file is an empty store. A file that is not JSON is set aside as <c>.corrupt</c> — kept,
    /// never deleted — and the store starts empty rather than the console refusing to open.
    /// </summary>
    public static void LoadTrust(PeerCertificateStore trustedAgents, string path)
    {
        ArgumentNullException.ThrowIfNull(trustedAgents);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        try
        {
            trustedAgents.Load(path);
        }
        catch (JsonException)
        {
            File.Move(path, path + ".corrupt", overwrite: true);
            trustedAgents.Load(path);
        }
    }

    public static PairResult ToResult(PairingFailure failure) => failure switch
    {
        PairingFailure.None => PairResult.Paired,
        PairingFailure.KeyNotProven => PairResult.WrongCode,
        PairingFailure.AgentUnreachable => PairResult.Unreachable,
        _ => PairResult.CannotPairNow,
    };

    public async Task<PairResult> PairAsync(AgentPc pc, string roomCode, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(pc);
        if (!RoomCode.IsValid(roomCode))
        {
            throw new ArgumentException("The room code must be exactly six ASCII digits.", nameof(roomCode));
        }

        var failure = await _pair(pc, Encoding.ASCII.GetBytes(roomCode), ct).ConfigureAwait(false);
        var result = ToResult(failure);
        if (result == PairResult.Paired)
        {
            _trustedAgents.Save(_trustPath);
        }

        return result;
    }

    public void Unpair(string deviceId)
    {
        _trustedAgents.Unpin(deviceId);
        _trustedAgents.Save(_trustPath);
    }

    public bool IsTrusted(string deviceId) => _trustedAgents.TryGetPinnedThumbprint(deviceId, out _);
}
