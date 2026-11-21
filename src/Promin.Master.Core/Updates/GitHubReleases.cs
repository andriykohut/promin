using System.Net;
using Promin.Protocol.Updates;

namespace Promin.Master.Core.Updates;

/// <summary>
/// Promin's releases as GitHub's API lists them. GitHub's latest release leaves out drafts and pre-releases, so a beta is
/// never offered as the latest. The request says only that it comes from Promin; nothing about the room is sent.
/// </summary>
public sealed class GitHubReleases(HttpClient http) : IReleaseSource
{
    private const string Api = "https://api.github.com/repos/andriykohut/promin/releases/";

    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);

    public Task<Release?> LatestAsync(CancellationToken ct) => GetAsync(Api + "latest", ct);

    public Task<Release?> ByVersionAsync(ReleaseVersion version, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(version);
        return GetAsync($"{Api}tags/v{version}", ct);
    }

    private async Task<Release?> GetAsync(string url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("Promin");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
        wait.CancelAfter(Wait);
        try
        {
            using var response = await http.SendAsync(request, wait.Token).ConfigureAwait(false);
            return response.StatusCode == HttpStatusCode.OK
                ? ReleaseJson.Parse(await response.Content.ReadAsStringAsync(wait.Token).ConfigureAwait(false))
                : null;
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
    }
}
