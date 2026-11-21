using System.Text.Json;
using System.Text.Json.Serialization;
using Promin.Protocol.Transport;

namespace Promin.Protocol.Updates;

/// <summary>
/// A setup's <c>.sig</c> file, published beside it in each release: <c>{"version":"0.4.0","signature":"&lt;base64&gt;"}</c>.
/// The signature is checked with <see cref="UpdateSignature.HoldsFor"/>.
/// </summary>
public sealed record SetupSignature(
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("signature")] byte[] Signature)
{
    /// <summary>Appended to the setup's file name.</summary>
    public const string Extension = ".sig";

    /// <summary>The file at <paramref name="path"/>; null when it is missing, unreadable, or lacks either field.</summary>
    public static SetupSignature? Read(string path)
    {
        try
        {
            var read = JsonSerializer.Deserialize<SetupSignature>(File.ReadAllBytes(path));
            return read is { Version: not null, Signature: not null } ? read : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Writes the file atomically, creating its folder if needed.</summary>
    public void Write(string path) => AtomicFile.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(this));
}
