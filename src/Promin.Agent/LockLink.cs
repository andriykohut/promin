using System.Net.Quic;
using System.Threading.Channels;
using Promin.Protocol.Messages;

namespace Promin.Agent;

/// <summary>
/// The lock stream of one console PC's connection: the console PC's lock and unlock messages are applied to this
/// PC, and the state is sent back after the first connect and after each message. The lock outlives the stream.
/// </summary>
/// <param name="mayLock">False when the console PC runs on this PC: it can unlock, never lock.</param>
/// <param name="log">Gets a line for each refused message, never its text.</param>
internal sealed class LockLink(ScreenLock screenLock, bool mayLock, Action<string> log)
{
    /// <summary>Runs until the stream or the connection ends or <paramref name="ct"/> is cancelled. Throws nothing; a failure that is not the end of the stream or connection is logged.</summary>
    public async Task RunAsync(IAgentConnection connection, CancellationToken ct)
    {
        try
        {
            var stream = await connection.AcceptLockStreamAsync(ct).ConfigureAwait(false);
            await stream.SendAsync(new LockState(screenLock.IsLocked), ct).ConfigureAwait(false);
            while (true)
            {
                switch (await stream.ReceiveAsync(ct).ConfigureAwait(false))
                {
                    case LockPc { Message: var message } when !mayLock || message.Length > LockLimits.MaxMessageLength:
                        log(mayLock
                            ? "A lock message longer than the limit was refused."
                            : "A lock message from the console PC on this PC was refused.");
                        break;
                    case LockPc { Message: var message }:
                        screenLock.Lock(message);
                        break;
                    case UnlockPc:
                        screenLock.Unlock();
                        break;
                }

                await stream.SendAsync(new LockState(screenLock.IsLocked), ct).ConfigureAwait(false);
            }
        }
        catch (Exception e) when (e is OperationCanceledException or IOException or ChannelClosedException or QuicException)
        {
            // The stream or the connection ended, or ct was cancelled; the connection's handler logs which.
        }
        catch (Exception e)
        {
            // The type only: a message could carry the text a person typed.
            log($"The lock stream ended after a failure: {e.GetType().Name}.");
        }
    }
}
