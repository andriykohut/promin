using System.Text.Json;

namespace Educast.Master.Core.Wall;

/// <summary>
/// The wall's settings as one JSON file (<c>%LocalAppData%\Educast\settings.json</c> in the app).
/// A missing or unreadable file means the defaults; a save writes a complete temporary file beside
/// the target and swaps it in. A save that fails keeps the choice for this run only.
/// </summary>
public sealed class JsonWallSettings : IWallSettings
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private readonly string _path;
    private Stored _stored;

    public JsonWallSettings(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
        _stored = Load(_path);
    }

    public string? QualityLabel
    {
        get => _stored.QualityLabel;
        set => Save(_stored with { QualityLabel = value });
    }

    public string? SharpnessName
    {
        get => _stored.SharpnessName;
        set => Save(_stored with { SharpnessName = value });
    }

    private static Stored Load(string path)
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<Stored>(File.ReadAllText(path), Options) ?? new Stored()
                : new Stored();
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            return new Stored();
        }
    }

    private void Save(Stored stored)
    {
        _stored = stored;
        var temporary = $"{_path}.{Guid.NewGuid():N}.tmp";
        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, stored, Options);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, _path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A quality choice that is not remembered is not worth a message; the choice holds for this run.
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private sealed record Stored(string? QualityLabel = null, string? SharpnessName = null);
}
