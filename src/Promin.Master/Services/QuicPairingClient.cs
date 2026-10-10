using System.Net;
using Promin.Master.Core.Rooms;
using Promin.Master.Core.Setup;
using Promin.Protocol.Transport;

namespace Promin.Master.Services;

/// <summary>
/// <see cref="IPairingClient"/> over the real transport. The mapping of the transport's verdict
/// and the saving of the trust file are <see cref="PairingClient"/>, in Core, where they are
/// tested; this class only dials.
/// </summary>
/// <remarks>
/// <c>PairAsync</c> does tens of milliseconds of arithmetic synchronously before its first
/// await, so it is started on the thread pool rather than on the UI thread.
/// </remarks>
public sealed class QuicPairingClient : IPairingClient
{
    private readonly PairingClient _inner;

    public QuicPairingClient(QuicMasterConnector connector, PeerCertificateStore trustedAgents, string trustPath)
    {
        ArgumentNullException.ThrowIfNull(connector);

        _inner = new PairingClient(
            (pc, presharedKey, ct) => Task.Run(
                async () =>
                {
                    var endpoint = new IPEndPoint(IPAddress.Parse(pc.Address), pc.Port);
                    var outcome = await connector
                        .PairAsync(endpoint, pc.DeviceId, presharedKey, ct)
                        .ConfigureAwait(false);
                    return outcome.Failure;
                },
                ct),
            trustedAgents,
            trustPath);
    }

    public Task<PairResult> PairAsync(StudentPc pc, string roomCode, CancellationToken ct) =>
        _inner.PairAsync(pc, roomCode, ct);

    public void Unpair(string deviceId) => _inner.Unpair(deviceId);

    public bool IsTrusted(string deviceId) => _inner.IsTrusted(deviceId);
}
