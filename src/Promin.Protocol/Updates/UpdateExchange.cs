using System.Net.Quic;
using Promin.Protocol.Messages;
using Promin.Protocol.Pairing;
using Promin.Protocol.Transport;

namespace Promin.Protocol.Updates;

/// <summary>
/// The console's end of the update stream, the bidirectional stream it opens right after connecting:
/// <see cref="AskVersion"/>, answered by <see cref="AgentVersion"/>; later an <see cref="OfferUpdate"/> with the setup's
/// raw bytes right behind it, answered by one <see cref="UpdateAnswer"/>. The agent may answer before taking every
/// byte, followed by <see cref="QuicControlChannel.StopReceiving"/>, as a file send's receiver does.
/// </summary>
public static class UpdateExchange
{
    /// <summary>The most setup bytes one write carries.</summary>
    private const int ChunkSize = 64 * 1024;

    /// <summary>Asks the agent for its version; null when the version it gives does not read as one.</summary>
    /// <exception cref="InvalidDataException">The agent answered with another message.</exception>
    /// <remarks>Any other exception means the stream or the connection ended.</remarks>
    public static async Task<ReleaseVersion?> AskVersionAsync(IControlChannel channel, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(channel);
        await channel.SendAsync(new AskVersion(), ct).ConfigureAwait(false);
        return await channel.ReceiveAsync(ct).ConfigureAwait(false) switch
        {
            AgentVersion { Version: var text } => ReleaseVersion.TryParse(text, out var version) ? version : null,
            var other => throw new InvalidDataException($"The agent answered {other.GetType().Name} to {nameof(AskVersion)}."),
        };
    }

    /// <summary>Offers the setup at <paramref name="setupPath"/> as <paramref name="version"/> and returns the agent's answer.</summary>
    /// <exception cref="InvalidDataException">The agent answered with another message.</exception>
    /// <remarks>
    /// An <see cref="IOException"/> from reading the setup, or any exception from the stream or the connection, leaves the
    /// stream unusable: the caller lets it go.
    /// </remarks>
    public static async Task<UpdateOutcome> SendAsync(
        IByteChannel channel, string setupPath, ReleaseVersion version, byte[] signature, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(setupPath);
        ArgumentNullException.ThrowIfNull(version);
        ArgumentNullException.ThrowIfNull(signature);

        await using var setup = File.OpenRead(setupPath);
        await channel.SendAsync(new OfferUpdate(version.ToString(), setup.Length, signature), ct).ConfigureAwait(false);

        // Read from the start: a refusal can arrive while the bytes are still going out.
        var reply = ReceiveAnswerAsync(channel, ct);
        try
        {
            var buffer = new byte[ChunkSize];
            int read;
            while ((read = await setup.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await channel.SendBytesAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            }
        }
        catch (QuicException e) when (e.ApplicationErrorCode == QuicControlChannel.RefusedErrorCode)
        {
            // The agent stopped receiving, which it does only after answering.
        }
        catch
        {
            // The caller lets the stream go, which ends the read too; the exception rethrown here is the one that counts.
            _ = reply.ContinueWith(static t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            throw;
        }

        return await reply.ConfigureAwait(false);
    }

    private static async Task<UpdateOutcome> ReceiveAnswerAsync(IControlChannel channel, CancellationToken ct) =>
        await channel.ReceiveAsync(ct).ConfigureAwait(false) switch
        {
            UpdateAnswer { Outcome: var outcome } => outcome,
            var other => throw new InvalidDataException($"The agent answered {other.GetType().Name} to {nameof(OfferUpdate)}."),
        };
}
