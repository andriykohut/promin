using System.Globalization;
using Promin.Protocol.Messages;

namespace Promin.Agent;

/// <summary>
/// What the agent does with the sends on one console PC's connection. Each is received into Desktop\Promin as the
/// person signed in to this session, never as SYSTEM, and one that arrives whole starts the helper, which opens what
/// was sent or shows the notice. One send at a time: the staging folder is one per person.
/// </summary>
/// <param name="person">The signed-in person, asked afresh for each send.</param>
/// <param name="log">Called as the agent itself, never inside the person's identity.</param>
internal sealed class FileReceiver(Func<ISignedInPerson> person, Action<string> log)
{
    /// <summary>
    /// Runs until the connection ends or <paramref name="ct"/> is cancelled, then waits for the sends under way. A
    /// send that arrives while another is being received is refused <see cref="SendOutcome.Busy"/>; one whose
    /// files are all in place, and is only starting the helper, does not hold the next. Throws nothing for what the
    /// connection or the console PC does.
    /// </summary>
    public async Task RunAsync(IAgentConnection connection, CancellationToken ct)
    {
        // Completes once the send being received is done with the staging folder.
        var exchanging = Task.CompletedTask;
        var sends = new List<Task>();
        try
        {
            while (true)
            {
                var send = await connection.AcceptSendAsync(ct).ConfigureAwait(false);
                if (exchanging.IsCompleted)
                {
                    var exchanged = new TaskCompletionSource();
                    exchanging = exchanged.Task;
                    sends.RemoveAll(running => running.IsCompleted);
                    sends.Add(Task.Run(() => ReceiveAsync(send, exchanged, ct), CancellationToken.None));
                    continue;
                }

                log($"A send arrived while another was being received, and was refused: {SendOutcome.Busy}.");
                await RefuseAsync(send, SendOutcome.Busy, ct).ConfigureAwait(false);
                await send.DisposeAsync().ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            // The connection ended, or ct was cancelled; the connection's handler logs which.
        }
        finally
        {
            await Task.WhenAll(sends).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Receives one send and starts the helper for it, completing <paramref name="exchanged"/> as soon as the send
    /// no longer uses the staging folder. Throws nothing.
    /// </summary>
    private async Task ReceiveAsync(IIncomingSend send, TaskCompletionSource exchanged, CancellationToken ct)
    {
        await using (send.ConfigureAwait(false))
        {
            // Until the exchange begins, nothing has answered the console PC.
            var exchanging = false;
            Exception? writeFailure = null;
            try
            {
                var signedIn = person();
                if (signedIn.Folders() is not { } folders)
                {
                    log($"Nobody is signed in on this PC, so a send was refused: {SendOutcome.Failed}.");
                    await RefuseAsync(send, SendOutcome.Failed, ct).ConfigureAwait(false);
                    return;
                }

                var promin = Path.Combine(folders.Desktop, "Promin");
                var target = new ReceivedFiles(Path.Combine(folders.LocalAppData, "Promin", "Incoming"), promin, FreeBytes);
                var outcome = await signedIn.RunAsAsync(() =>
                {
                    exchanging = true;

                    // Kept, not logged: this runs as the person, and the log is locked to SYSTEM.
                    return send.ReceiveAsync(target, ct, failure => writeFailure = failure);
                }).ConfigureAwait(false);
                exchanged.TrySetResult();

                var why = (target.Failure ?? writeFailure) is { } reason ? $" {reason.Message}" : "";
                log($"A send ended: {outcome}, {target.TopLevel.Count} items, {send.Header.TotalBytes} bytes.{why}");
                if (outcome != SendOutcome.Sent)
                {
                    return;
                }

                try
                {
                    await signedIn.RunHelperAsync(
                        [
                            "--received",
                            target.TopLevel.Count.ToString(CultureInfo.InvariantCulture),
                            promin,
                            .. OpenPlan.For(target.TopLevel, send.Header.Open, promin),
                        ],
                        TimeSpan.Zero).ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    log($"The helper that opens what was sent could not be started: {e.Message}");
                }
            }
            catch (Exception e) when (!exchanging)
            {
                log($"Received files cannot be placed, so a send was refused: {SendOutcome.Failed}. {e.Message}");
                await RefuseAsync(send, SendOutcome.Failed, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
            }
            catch (Exception e)
            {
                var before = writeFailure is { } failure ? $" Before that, it could not be written: {failure.Message}" : "";
                log($"A send ended early: {e.Message}{before}");
            }
            finally
            {
                exchanged.TrySetResult();
            }
        }
    }

    /// <summary>Answers <paramref name="outcome"/>; a send that has already ended is left as it is.</summary>
    private static async Task RefuseAsync(IIncomingSend send, SendOutcome outcome, CancellationToken ct)
    {
        try
        {
            await send.RefuseAsync(outcome, ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The stream or the connection has ended, so there is nobody to tell.
        }
    }

    /// <summary>
    /// Free bytes on the drive of <paramref name="path"/>, or <see cref="long.MaxValue"/> when its root is not a
    /// drive (a Desktop redirected to a network share): a full share then fails the send as a write error.
    /// </summary>
    internal static long FreeBytes(string path)
    {
        try
        {
            return new DriveInfo(Path.GetPathRoot(path)!).AvailableFreeSpace;
        }
        catch (ArgumentException)
        {
            return long.MaxValue;
        }
    }
}
