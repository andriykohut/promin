using System.Globalization;
using Promin.Master.Core.Rooms;
using Promin.Master.Core.Tests.TestDoubles;
using Promin.Master.Core.Updates;
using Promin.Master.Core.Wall;
using Promin.Protocol.Updates;

namespace Promin.Master.Core.Tests.Wall;

/// <summary>The wall with an update offer and a running update, over the fakes and a real <see cref="RoomUpdate"/>.</summary>
public sealed class RoomWallUpdateTests : IDisposable
{
    private static readonly DateOnly PairedOn = new(2026, 9, 22);

    private readonly string _directory = Directory.CreateTempSubdirectory("promin-wall-update-").FullName;
    private readonly InMemoryRoomStore _store = new();
    private readonly FakeDiscovery _discovery = new();
    private readonly FakeBroadcastController _broadcast = new();
    private readonly FakeRoomConnections _connections = new();
    private readonly FakePairingClient _pairing = new();
    private readonly InMemoryWallSettings _settings = new();
    private readonly FakeReleaseSource _releases = new();
    private readonly FakeUpdateDownloads _downloads = new();
    private DateTimeOffset _now = new(2026, 11, 21, 10, 0, 0, TimeSpan.Zero);

    public RoomWallUpdateTests()
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static string Id(int n) => $"device-{n:00}";

    private static PcTile Tile(RoomWallViewModel vm, int n) => vm.Tiles.Single(t => t.DeviceId == Id(n));

    private static ReleaseVersion V(string text) =>
        ReleaseVersion.TryParse(text, out var version) ? version : throw new FormatException(text);

    private static Release Published(string version)
    {
        var at = $"https://github.com/andriykohut/promin/releases/download/v{version}/";
        return new Release(V(version), new(at + "a.exe"), new(at + "a.exe.sig"), new(at + "c.exe"), new(at + "c.exe.sig"));
    }

    private void See(params int[] numbers)
    {
        foreach (var n in numbers)
        {
            _discovery.Raise(new AgentPc(Id(n), $"PC-{n:00}", $"192.168.1.{20 + n}", 17843));
        }
    }

    /// <summary>Three ticked members on a console of <paramref name="own"/>, GitHub's latest at <paramref name="latest"/>; <paramref name="seen"/> are online.</summary>
    private async Task<(RoomWallViewModel Wall, RoomUpdate Update)> WallAsync(string own, string latest, params int[] seen)
    {
        for (var n = 1; n <= 3; n++)
        {
            _store.Members.Add(new RoomMember(Id(n), $"PC-{n:00}", $"192.168.1.{20 + n}", 17843, PairedOn, true));
        }

        _releases.Latest = Published(latest);
        var update = new RoomUpdate(V(own), _releases, _downloads, _connections, new FakeSelfUpdate(), Path.Combine(_directory, "pending.json"), () => _now, a => a());
        await update.CheckAsync(named: null);
        var vm = new RoomWallViewModel(_store, _discovery, _broadcast, _connections, _pairing, _settings, () => _now, a => a(), update: update);
        vm.Activate();
        See(seen);
        vm.Tick();
        return (vm, update);
    }

    [Fact]
    public async Task TheOffer_BeatsNoPcsOnTheNetwork()
    {
        var (vm, _) = await WallAsync("0.3.0", "0.4.0");

        Assert.Equal("Promin 0.4.0 is out", vm.MessageTitle);
        Assert.True(vm.MessageActionUpdates);
        Assert.True(vm.CanUpdate);
    }

    [Fact]
    public async Task ARoomThatCannotBeRead_BeatsTheOffer()
    {
        _store.LoadThrows = new IOException("locked");
        var (vm, _) = await WallAsync("0.3.0", "0.4.0", 1);

        Assert.Equal(WallMessage.RoomUnreadable, vm.Message);
        Assert.False(vm.MessageActionUpdates);
    }

    [Fact]
    public async Task TheRunningUpdate_BeatsEverything()
    {
        _store.LoadThrows = new IOException("locked");
        var download = new TaskCompletionSource<UpdateSet?>();
        _downloads.Get = (_, _) => download.Task;
        var (vm, update) = await WallAsync("0.3.0", "0.4.0");

        var running = vm.UpdateAsync();

        Assert.Equal("Updating to Promin 0.4.0", vm.MessageTitle);
        Assert.True(vm.IsUpdating);
        download.SetResult(null);
        await running;
        Assert.False(update.IsRunning);
    }

    [Fact]
    public async Task NoUpdate_OnAir()
    {
        var (vm, _) = await WallAsync("0.3.0", "0.4.0", 1, 2, 3);
        await vm.StartAsync();

        Assert.False(vm.CanUpdate);
    }

    [Fact]
    public async Task NoUpdate_WhileAPcIsViewed()
    {
        var (vm, _) = await WallAsync("0.3.0", "0.4.0", 1, 2, 3);
        _connections.NextSession = new FakeControlSession();
        Assert.NotNull(await vm.StartViewingAsync(Tile(vm, 1)));

        Assert.False(vm.CanUpdate);
    }

    [Fact]
    public async Task NoUpdate_WithAPcLocked()
    {
        var (vm, _) = await WallAsync("0.3.0", "0.4.0", 1, 2, 3);
        _connections.RaiseLock(Id(2), true);
        vm.Tick();

        Assert.False(vm.CanUpdate);
    }

    [Fact]
    public async Task NothingElse_WhileUpdating()
    {
        var download = new TaskCompletionSource<UpdateSet?>();
        _downloads.Get = (_, _) => download.Task;
        var (vm, _) = await WallAsync("0.3.0", "0.4.0", 1, 2, 3);
        Assert.True(vm.CanStart);

        var running = vm.UpdateAsync();

        Assert.False(vm.CanStart);
        Assert.False(vm.CanLock);
        Assert.False(vm.CanPower);
        Assert.False(vm.CanUpdate);
        Assert.All(vm.Tiles, t => Assert.False(t.CanControl));
        download.SetResult(null);
        await running;
        Assert.True(vm.CanStart);
    }

    [Fact]
    public async Task AnUpdatingPc_ReadsUpdating_AndKeepsItsLastPicture_WhileItsAgentRestarts()
    {
        _downloads.Cached[V("0.4.0")] = new UpdateFile("cached.exe", V("0.4.0"), [9]);
        var (vm, _) = await WallAsync("0.4.0", "0.4.0", 1, 2, 3);
        _connections.RaiseVersion(Id(1), "0.3.0");
        _connections.RaisePicture(Id(1), [0xFF, 0xD8, 1]);
        vm.Tick();

        await vm.UpdateAsync();
        Assert.Equal(PcTileState.Updating, Tile(vm, 1).State);
        Assert.Equal("Updating", Tile(vm, 1).StatusText);

        // PC-01 stops announcing itself while its agent restarts; the others carry on.
        _connections.RaiseEnded(Id(1));
        _now += TimeSpan.FromSeconds(30);
        See(2, 3);
        vm.Tick();
        Assert.Equal(PcTileState.Updating, Tile(vm, 1).State);
        Assert.Equal([0xFF, 0xD8, 1], Tile(vm, 1).Picture);
        Assert.Equal(PcTileState.Ready, Tile(vm, 2).State);
    }

    [Fact]
    public async Task AnOutOfDatePc_IsMarked()
    {
        var (vm, _) = await WallAsync("0.4.0", "0.4.0", 1, 2, 3);
        _connections.RaiseVersion(Id(1), "0.3.0");
        _connections.RaiseVersion(Id(2), "0.4.0");
        vm.Tick();

        Assert.True(Tile(vm, 1).IsOutOfDate);
        Assert.Equal("Promin 0.3.0, older than this console", Tile(vm, 1).OutOfDateTip);
        Assert.False(Tile(vm, 2).IsOutOfDate);
        Assert.Equal("PC-01 has an older Promin", vm.MessageTitle);
    }

    [Fact]
    public void WithoutAnUpdate_TheWallIsAsBefore()
    {
        _store.Members.Add(new RoomMember(Id(1), "PC-01", "192.168.1.21", 17843, PairedOn, true));
        var vm = new RoomWallViewModel(_store, _discovery, _broadcast, _connections, _pairing, _settings, () => _now, a => a());
        vm.Activate();

        Assert.Equal(WallMessage.NoPcReachable, vm.Message);
        Assert.False(vm.CanUpdate);
        Assert.False(vm.IsUpdating);
    }
}
