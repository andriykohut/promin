using Promin.Master.Core.Rooms;

namespace Promin.Master.Core.Tests.Rooms;

public sealed class JsonRoomStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "promin-tests", Guid.NewGuid().ToString("N"));
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
    public void Load_WhenTheFileCannotBeRead_ThrowsRoomStoreException()
    {
        File.WriteAllText(_path, "[]");
        using var lock_ = new FileStream(_path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var error = Assert.Throws<RoomStoreException>(() => new JsonRoomStore(_path).Load());

        Assert.IsAssignableFrom<IOException>(error.InnerException);
        Assert.False(File.Exists(_path + ".corrupt"));
    }

    [Fact]
    public void Save_WhenTheFileIsLocked_ThrowsRoomStoreException()
    {
        File.WriteAllText(_path, "[]");
        using var lock_ = new FileStream(_path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        Assert.Throws<RoomStoreException>(() =>
            new JsonRoomStore(_path).Save([new RoomMember("a1", "PC-01", "192.168.1.21", 17843, new DateOnly(2026, 10, 3), true)]));
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
    }

    [Fact]
    public void Load_SkipsAMemberWithNoNameOrAddress_AndKeepsTheRest()
    {
        File.WriteAllText(_path, """
            [
              {"DeviceId":"a1","DisplayName":"PC-01","LastAddress":"192.168.1.21","Port":17843,"PairedOn":"2026-10-03","Selected":true},
              {"DeviceId":"b2","DisplayName":null,"LastAddress":"192.168.1.22","Port":17843,"PairedOn":"2026-10-03","Selected":true},
              {"DeviceId":"c3","DisplayName":"PC-03","LastAddress":"","Port":17843,"PairedOn":"2026-10-03","Selected":true},
              {"DeviceId":"","DisplayName":"PC-04","LastAddress":"192.168.1.24","Port":17843,"PairedOn":"2026-10-03","Selected":true}
            ]
            """);

        var loaded = new JsonRoomStore(_path).Load();

        Assert.Equal(["a1"], loaded.Select(m => m.DeviceId));
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
