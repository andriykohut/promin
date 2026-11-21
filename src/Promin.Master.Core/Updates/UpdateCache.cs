using System.Net;
using Promin.Protocol.Updates;

namespace Promin.Master.Core.Updates;

/// <summary>A release's setups, downloaded and checked: the agent setup always, the console setup when this PC needs it.</summary>
public sealed record UpdateSet(UpdateFile Agent, UpdateFile? Console);

/// <summary>Where the console gets the setups an update runs and sends.</summary>
public interface IUpdateDownloads
{
    /// <summary>
    /// The agent setup of <paramref name="release"/>, and its console setup when <paramref name="withConsole"/>, each
    /// with a <c>.sig</c> for that version whose signature holds. Null when anything cannot be downloaded or does not
    /// check; only cancellation throws.
    /// </summary>
    Task<UpdateSet?> GetAsync(Release release, bool withConsole, CancellationToken ct);

    /// <summary>The agent setup of <paramref name="version"/> already downloaded, if it still checks; otherwise null.</summary>
    UpdateFile? CachedAgent(ReleaseVersion version);
}

/// <summary>
/// The setups of the newest release this console has fetched, one folder per version under
/// <paramref name="directory"/>. A version's files are kept for as long as it is the newest, so an agent PC that was off
/// can be updated later without internet; older versions' folders go once a newer one is complete.
/// </summary>
/// <remarks>Each file is downloaded beside its final name first: nothing half-written is ever taken for a setup.</remarks>
public sealed class UpdateCache(string directory, HttpClient http, ReadOnlyMemory<byte> publicKey) : IUpdateDownloads
{
    private const string PartSuffix = ".part";

    public async Task<UpdateSet?> GetAsync(Release release, bool withConsole, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(release);
        var folder = Folder(release.Version);
        var agent = await GetOneAsync(folder, ReleaseJson.AgentSetupName, release.AgentSetup, release.AgentSignature, release.Version, ct)
            .ConfigureAwait(false);
        var console = agent is not null && withConsole
            ? await GetOneAsync(folder, ReleaseJson.ConsoleSetupName, release.ConsoleSetup, release.ConsoleSignature, release.Version, ct)
                .ConfigureAwait(false)
            : null;

        if (agent is null || (withConsole && console is null))
        {
            TryDeleteFolder(folder);
            return null;
        }

        foreach (var other in Directory.EnumerateDirectories(directory))
        {
            if (!string.Equals(Path.GetFullPath(other), Path.GetFullPath(folder), StringComparison.OrdinalIgnoreCase))
            {
                TryDeleteFolder(other);
            }
        }

        return new UpdateSet(agent, console);
    }

    public UpdateFile? CachedAgent(ReleaseVersion version)
    {
        ArgumentNullException.ThrowIfNull(version);
        return Checked(Path.Combine(Folder(version), ReleaseJson.AgentSetupName), version);
    }

    private string Folder(ReleaseVersion version) => Path.Combine(directory, version.ToString());

    private async Task<UpdateFile?> GetOneAsync(string folder, string name, Uri setupUri, Uri signatureUri, ReleaseVersion version, CancellationToken ct)
    {
        var setup = Path.Combine(folder, name);
        if (Checked(setup, version) is { } kept)
        {
            return kept;
        }

        Directory.CreateDirectory(folder);
        return await DownloadAsync(signatureUri, setup + SetupSignature.Extension, ct).ConfigureAwait(false)
            && await DownloadAsync(setupUri, setup, ct).ConfigureAwait(false)
                ? Checked(setup, version)
                : null;
    }

    /// <summary>The setup at <paramref name="setup"/> when its <c>.sig</c> is for <paramref name="version"/> and holds; otherwise null.</summary>
    private UpdateFile? Checked(string setup, ReleaseVersion version) =>
        SetupSignature.Read(setup + SetupSignature.Extension) is { } sig
        && ReleaseVersion.TryParse(sig.Version, out var signedFor)
        && signedFor == version
        && UpdateSignature.HoldsFor(setup, Path.GetFileName(setup), version, sig.Signature, publicKey.Span)
            ? new UpdateFile(setup, version, sig.Signature)
            : null;

    private async Task<bool> DownloadAsync(Uri uri, string path, CancellationToken ct)
    {
        var part = path + PartSuffix;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd("Promin");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                return false;
            }

            var file = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None);
            await using (file.ConfigureAwait(false))
            {
                await response.Content.CopyToAsync(file, ct).ConfigureAwait(false);
            }

            File.Move(part, path, overwrite: true);
            return true;
        }
        catch (Exception e) when (e is HttpRequestException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
        finally
        {
            try
            {
                File.Delete(part);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Removed with its folder when the download is given up.
            }
        }
    }

    private static void TryDeleteFolder(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A setup still open, such as one the console just ran; the next complete download tries again.
        }
    }
}
