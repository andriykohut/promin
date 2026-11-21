using System.Threading.Channels;
using Promin.Protocol.Messages;
using Promin.Protocol.Pairing;
using Promin.Protocol.Updates;

namespace Promin.Agent;

/// <summary>
/// The update stream of one console PC's connection. The agent says its version as the stream opens, then answers each
/// <see cref="OfferUpdate"/>. A setup newer than <paramref name="installed"/> whose signature holds for
/// <paramref name="publicKey"/> is saved in <paramref name="updateDirectory"/> with its <c>.sig</c>, and
/// <paramref name="accepted"/> is called: the host then exits with <see cref="ExitCodes.UpdateReady"/>, and the service
/// checks the setup again and runs it. Anything else is answered with why, and nothing of it is kept.
/// </summary>
/// <remarks>
/// Every byte of an offer is read before it is answered, a refusal's too, so the stream stays in step and the console
/// never waits on writes nobody reads. <paramref name="updateDirectory"/> lies inside the agent's data folder, which
/// <c>--install</c> locks to SYSTEM and administrators, so the person at the PC cannot swap the file once checked.
/// </remarks>
/// <param name="log">Gets one line per offer: its version and the answer.</param>
internal sealed class UpdateLink(
    ReleaseVersion installed, string updateDirectory, ReadOnlyMemory<byte> publicKey, Action accepted, Action<string> log)
{
    /// <summary>The saved setup's file name in the update folder; its signature is beside it with <see cref="SetupSignature.Extension"/>.</summary>
    public const string SetupName = "Promin-Agent-Setup.exe";

    private const string PartSuffix = ".part";

    /// <summary>Runs until the stream or the connection ends, <paramref name="ct"/> is cancelled, or an update is accepted. Throws nothing.</summary>
    public async Task RunAsync(IAgentConnection connection, CancellationToken ct)
    {
        try
        {
            var stream = await connection.AcceptUpdateStreamAsync(ct).ConfigureAwait(false);
            await stream.SendAsync(new AgentVersion(installed.ToString()), ct).ConfigureAwait(false);
            while (true)
            {
                if (await stream.ReceiveAsync(ct).ConfigureAwait(false) is not OfferUpdate offer)
                {
                    continue;
                }

                var outcome = await TakeAsync(stream, offer, ct).ConfigureAwait(false);
                log($"An update to {offer.Version} was offered: {outcome}.");
                await stream.SendAsync(new UpdateAnswer(outcome), ct).ConfigureAwait(false);
                if (outcome == UpdateOutcome.Accepted)
                {
                    accepted();
                    return;
                }
            }
        }
        catch (Exception e) when (e is OperationCanceledException or IOException or ChannelClosedException or ControlMessageFormatException)
        {
            // The stream or the connection ended, or ct was cancelled; the connection's handler logs which.
        }
        catch (Exception e)
        {
            log($"The update stream ended after a failure: {e.GetType().Name}: {e.Message}");
        }
    }

    /// <summary>Removes whatever an earlier update left in the folder. A file still in use, by a setup still finishing, stays until the next start.</summary>
    public static void ClearLeftovers(string updateDirectory, Action<string> log)
    {
        if (!Directory.Exists(updateDirectory))
        {
            return;
        }

        foreach (var entry in Directory.EnumerateFileSystemEntries(updateDirectory))
        {
            try
            {
                if (Directory.Exists(entry))
                {
                    Directory.Delete(entry, recursive: true);
                }
                else
                {
                    File.Delete(entry);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                log($"{Path.GetFileName(entry)}, left by an update, could not be removed yet: {e.Message}");
            }
        }
    }

    /// <summary>Reads the offer's bytes and returns the answer. Throws, keeping nothing, when the stream ends first.</summary>
    private async Task<UpdateOutcome> TakeAsync(IByteChannel stream, OfferUpdate offer, CancellationToken ct)
    {
        var setup = Path.Combine(updateDirectory, SetupName);
        var part = setup + PartSuffix;

        // The signature covers the version, so a version that does not read cannot carry a valid one.
        if (!ReleaseVersion.TryParse(offer.Version, out var version) || version <= installed)
        {
            await stream.ReceiveBytesAsync(Stream.Null, offer.Length, ct).ConfigureAwait(false);
            return version is null ? UpdateOutcome.BadSignature : UpdateOutcome.NotNewer;
        }

        try
        {
            var file = SaveOrDiscard.Open(part);
            await using (file.ConfigureAwait(false))
            {
                await stream.ReceiveBytesAsync(file, offer.Length, ct).ConfigureAwait(false);
                if (await file.CloseAsync().ConfigureAwait(false) is { } failure)
                {
                    log($"The setup for {version} could not be saved: {failure.Message}");
                    return UpdateOutcome.Failed;
                }
            }

            if (!UpdateSignature.HoldsFor(part, SetupName, version, offer.Signature, publicKey.Span))
            {
                return UpdateOutcome.BadSignature;
            }

            try
            {
                File.Move(part, setup, overwrite: true);
                new SetupSignature(version.ToString(), offer.Signature).Write(setup + SetupSignature.Extension);
                return UpdateOutcome.Accepted;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                log($"The setup for {version} could not be put in place: {e.Message}");
                TryDelete(setup);
                TryDelete(setup + SetupSignature.Extension);
                return UpdateOutcome.Failed;
            }
        }
        finally
        {
            TryDelete(part);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A folder in the way, or a file held open: neither is a setup this link put in place.
        }
    }

    /// <summary>
    /// Where an offer's bytes go: the file while it can be written, then nowhere. A failure to create or write the file
    /// is kept for <see cref="CloseAsync"/>, and the bytes still read on, so the stream stays in step.
    /// </summary>
    private sealed class SaveOrDiscard : Stream
    {
        private FileStream? _file;
        private Exception? _failure;

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public static SaveOrDiscard Open(string path)
        {
            var target = new SaveOrDiscard();
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                target._file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                target._failure = e;
            }

            return target;
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_file is null)
            {
                return;
            }

            try
            {
                await _file.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                _failure = e;
                await DropFileAsync().ConfigureAwait(false);
            }
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Write(byte[] buffer, int offset, int count) =>
            WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        /// <summary>Flushes and closes the file; returns what kept it from being saved whole, or null.</summary>
        public async Task<Exception?> CloseAsync()
        {
            if (_file is not null)
            {
                try
                {
                    await _file.FlushAsync().ConfigureAwait(false);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    _failure = e;
                }

                await DropFileAsync().ConfigureAwait(false);
            }

            return _failure;
        }

        public override async ValueTask DisposeAsync()
        {
            await DropFileAsync().ConfigureAwait(false);
            await base.DisposeAsync().ConfigureAwait(false);
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        private async Task DropFileAsync()
        {
            var file = _file;
            _file = null;
            if (file is not null)
            {
                try
                {
                    await file.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    _failure ??= e;
                }
            }
        }
    }
}
