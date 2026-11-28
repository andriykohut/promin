using System.Text.Json;
using Promin.Protocol.Updates;

namespace Promin.Master.Core.Updates;

/// <summary>Reads a release from GitHub's release JSON (<c>tag_name</c>, and each asset's <c>name</c> and <c>browser_download_url</c>).</summary>
public static class ReleaseJson
{
    public const string AgentSetupName = "Promin-Agent-Setup.exe";

    public const string ConsoleSetupName = "Promin-Console-Setup.exe";

    /// <summary>
    /// The release; null when the tag does not read as a version, when either setup or either <c>.sig</c> is missing or
    /// not served over HTTPS, or when the JSON is not a release.
    /// </summary>
    public static Release? Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("tag_name", out var tag)
                || !ReleaseVersion.TryParse(tag.GetString(), out var version)
                || !root.TryGetProperty("assets", out var assets)
                || assets.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var urls = new Dictionary<string, Uri>(StringComparer.Ordinal);
            foreach (var asset in assets.EnumerateArray())
            {
                if (asset.TryGetProperty("name", out var name) && name.GetString() is { } file
                    && asset.TryGetProperty("browser_download_url", out var url)
                    && Uri.TryCreate(url.GetString(), UriKind.Absolute, out var uri)
                    && uri.Scheme == Uri.UriSchemeHttps)
                {
                    urls[file] = uri;
                }
            }

            var signature = SetupSignature.Extension;
            return urls.TryGetValue(AgentSetupName, out var agent)
                && urls.TryGetValue(AgentSetupName + signature, out var agentSig)
                && urls.TryGetValue(ConsoleSetupName, out var console)
                && urls.TryGetValue(ConsoleSetupName + signature, out var consoleSig)
                    ? new Release(version, agent, agentSig, console, consoleSig)
                    : null;
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException)
        {
            return null;
        }
    }
}
