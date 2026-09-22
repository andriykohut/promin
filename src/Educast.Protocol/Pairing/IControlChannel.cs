using Educast.Protocol.Messages;

namespace Educast.Protocol.Pairing;

public interface IControlChannel
{
    Task SendAsync(ControlMessage message, CancellationToken ct);
    Task<ControlMessage> ReceiveAsync(CancellationToken ct);
}
