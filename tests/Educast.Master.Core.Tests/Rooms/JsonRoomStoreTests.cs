using Educast.Master.Core.Rooms;

namespace Educast.Master.Core.Tests.Rooms;

public sealed class JsonRoomStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "educast-tests", Guid.NewGuid().ToString("N"));
    private readonly string _path;

    public JsonRoomStoreTests()
    {
        Directory.CreateDirectory(_directory);
        _path = Path.Combine(_directory, "room.json");
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void Load_WhenTheFileIsMissing_IsEmpty()
    {
        var store = new JsonRoomStore(_path);

        Assert.Empty(store.Load());
    }

    [Fact]
    public void SaveThenLoad_RoundTripsEveryField()
    {
        var members = new[]
        {
            new RoomMember("a1", "PC-01", "192.168.1.21", 17843, new DateOnly(2026, 10, 3), Selected: true),
            new RoomMember("b2", "Front left", "192.168.1.22", 17900, new DateOnly(2026, 9, 1), Selected: false),
        };

        new JsonRoomStore(_path).Save(members);
        var loaded = new JsonRoomStore(_path).Load();

        Assert.Equal(members, loaded);
    }

    [Fact]
    public void Load_WhenTheFileIsCorrupt_IsEmpty_AndKeepsTheBadFile()
    {
        File.WriteAllText(_path, "{not json");

        var loaded = new JsonRoomStore(_path).Load();

        Assert.Empty(loaded);
        Assert.Equal("{not json", File.ReadAllText(_path + ".corrupt"));
    }

    [Fact]
    public void Save_ReplacesTheFileAtomically()
    {
        var store = new JsonRoomStore(_path);
        store.Save([new RoomMember("a1", "PC-01", "192.168.1.21", 17843, new DateOnly(2026, 10, 3), true)]);
        store.Save([new RoomMember("a1", "PC-01", "192.168.1.21", 17843, new DateOnly(2026, 10, 3), false)]);

        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
        Assert.False(Assert.Single(store.Load()).Selected);
    }
}
