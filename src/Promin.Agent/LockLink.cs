using System.Net.Quic;
using System.Threading.Channels;
using Promin.Protocol.Messages;
using Promin.Protocol.Pairing;

namespace Promin.Agent;

/// <summary>
/// The lock stream of one console PC's connection: the console PC's lock and unlock messages are applied to this
/// PC, and the state is sent back after the first connect, after each message and when the lock changes without a message
/// (<see cref="ScreenLock.Changed"/>). The lock outlives the stream.
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
            await ExchangeAsync(stream, ct).ConfigureAwait(false);
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

    // One loop does all the sending: a state read by a second sender could land after a newer one and stay on the wall.
    private async Task ExchangeAsync(IControlChannel stream, CancellationToken ct)
    {
        using var ending = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var changed = Channel.CreateUnbounded<bool>();
        void OnChanged() => changed.Writer.TryWrite(true);
        screenLock.Changed += OnChanged;
        var received = stream.ReceiveAsync(ending.Token);
        var report = changed.Reader.ReadAsync(ending.Token).AsTask();
        try
        {
            while (true)
            {
                await stream.SendAsync(new LockState(screenLock.IsLocked), ending.Token).ConfigureAwait(false);
                if (await Task.WhenAny(received, report).ConfigureAwait(false) == received)
                {
                    Obey(await received.ConfigureAwait(false));
                    received = stream.ReceiveAsync(ending.Token);
                }
                else
                {
                    await report.ConfigureAwait(false);
                    report = changed.Reader.ReadAsync(ending.Token).AsTask();
                }
            }
        }
        finally
        {
            screenLock.Changed -= OnChanged;

            // The read under way ends here: the stream is disposed once this has returned.
            await ending.CancelAsync().ConfigureAwait(false);
            await ((Task)received).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    private void Obey(ControlMessage received)
    {
        switch (received)
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
    }
}
