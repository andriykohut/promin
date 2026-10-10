using Promin.Master.Core.Wall;

namespace Promin.Master.Core.Tests.Wall;

public sealed class JsonWallSettingsTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "promin-tests", Guid.NewGuid().ToString("N"));
    private readonly string _path;

    public JsonWallSettingsTests()
    {
        Directory.CreateDirectory(_directory);
        _path = Path.Combine(_directory, "settings.json");
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void AMissingFile_MeansNothingRemembered()
    {
        var settings = new JsonWallSettings(_path);

        Assert.Null(settings.QualityLabel);
        Assert.Null(settings.SharpnessName);
        Assert.False(File.Exists(_path));
    }

    [Fact]
    public void AChoice_IsThereForTheNextRun()
    {
        var settings = new JsonWallSettings(_path);
        settings.QualityLabel = "720p, 15 fps";
        settings.SharpnessName = "Light";

        var next = new JsonWallSettings(_path);

        Assert.Equal("720p, 15 fps", next.QualityLabel);
        Assert.Equal("Light", next.SharpnessName);
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
    }

    [Fact]
    public void ACorruptFile_MeansNothingRemembered_AndIsReplacedByTheNextSave()
    {
        File.WriteAllText(_path, "{not json");

        var settings = new JsonWallSettings(_path);
        Assert.Null(settings.QualityLabel);

        settings.QualityLabel = "1080p, 10 fps";
        Assert.Equal("1080p, 10 fps", new JsonWallSettings(_path).QualityLabel);
    }
}
