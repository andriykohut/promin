using Promin.Master.Core.Tests.TestDoubles;
using Promin.Master.Core.Updates;
using Promin.Master.Core.Wall;
using Promin.Protocol.Messages;
using Promin.Protocol.Updates;

namespace Promin.Master.Core.Tests.Updates;

/// <summary>The room's update, over fake connections, releases, downloads and elevation, on a clock the tests move.</summary>
public sealed class RoomUpdateTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("promin-room-update-").FullName;
    private readonly FakeReleaseSource _releases = new();
    private readonly FakeUpdateDownloads _downloads = new();
    private readonly FakeRoomConnections _connections = new();
    private readonly FakeSelfUpdate _self = new();
    private DateTimeOffset _now = new(2026, 11, 21, 10, 0, 0, TimeSpan.Zero);
    private int _exits;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private string PendingPath => Path.Combine(_directory, "pending.json");

    private static ReleaseVersion V(string text) =>
        ReleaseVersion.TryParse(text, out var version) ? version : throw new FormatException(text);

    private static Release Published(string version)
    {
        var at = $"https://github.com/andriykohut/promin/releases/download/v{version}/";
        return new Release(V(version), new(at + "a.exe"), new(at + "a.exe.sig"), new(at + "c.exe"), new(at + "c.exe.sig"));
    }

    private static string? NameOf(string id) => id.Replace("device-", "PC-", StringComparison.Ordinal);

    /// <summary>A console on <paramref name="own"/> that has checked, with GitHub's latest at <paramref name="latest"/>.</summary>
    private async Task<RoomUpdate> ConsoleAsync(string own, string? latest)
    {
        _releases.Latest = latest is null ? null : Published(latest);
        var update = new RoomUpdate(V(own), _releases, _downloads, _connections, _self, PendingPath, () => _now, a => a());
        update.ExitRequested += () => _exits++;
        await update.CheckAsync(named: null);
        return update;
    }

    private WallMessage? Message(RoomUpdate update) => update.Message(NameOf);

    [Fact]
    public async Task NoOffer_WhenNothingIsNewer_AndNoPcIsBehind()
    {
        var update = await ConsoleAsync("0.4.0", "0.4.0");
        _connections.RaiseVersion("device-01", "0.4.0");

        Assert.Null(Message(update));
        Assert.Null(update.OutOfDate("device-01"));
    }

    [Fact]
    public async Task NoOffer_WhenGitHubCannotBeReached()
    {
        var update = await ConsoleAsync("0.3.0", latest: null);
        Assert.Null(Message(update));
    }

    [Fact]
    public async Task ANewerRelease_IsOffered()
    {
        var update = await ConsoleAsync("0.3.0", "0.4.0");
        _connections.RaiseVersion("device-01", "0.3.0");
        _connections.RaiseVersion("device-02", "0.3.0");
        _connections.RaiseVersion("device-03", "0.4.0");

        var message = Message(update)!;
        Assert.Equal(WallMessageKind.Info, message.Kind);
        Assert.Equal("Promin 0.4.0 is out", message.Title);
        Assert.Equal("This PC and 2 others have 0.3.0. Each PC drops off for about a minute while it updates.", message.Text);
        Assert.Equal("Update", message.ActionLabel);
        Assert.True(message.ActionUpdates);
    }

    [Fact]
    public async Task ANewerRelease_WithNoPcBehind_NamesOnlyThisPc()
    {
        var update = await ConsoleAsync("0.3.0", "0.4.0");
        Assert.Equal("This PC has 0.3.0.", Message(update)!.Text);
    }

    [Theory]
    [InlineData(new[] { 3 }, "PC-03 has an older Promin", "This PC has 0.4.0 and PC-03 has 0.3.0. It drops off for about a minute while it updates.")]
    [InlineData(new[] { 7, 3 }, "PC-03 and PC-07 have an older Promin", "This PC has 0.4.0. Each PC drops off for about a minute while it updates.")]
    [InlineData(new[] { 9, 3, 1, 7 }, "PC-01, PC-03 and 2 others have an older Promin", "This PC has 0.4.0. Each PC drops off for about a minute while it updates.")]
    public async Task ThePcsBehindTheConsole_AreNamed(int[] behind, string title, string text)
    {
        var update = await ConsoleAsync("0.4.0", "0.4.0");
        foreach (var n in behind)
        {
            _connections.RaiseVersion($"device-{n:00}", "0.3.0");
        }

        _connections.RaiseVersion("device-05", "0.4.0");

        var message = Message(update)!;
        Assert.Equal((title, text), (message.Title, message.Text));
        Assert.True(message.ActionUpdates);
        Assert.Equal(V("0.3.0"), update.OutOfDate("device-03"));
        Assert.Null(update.OutOfDate("device-05"));
    }

    [Fact]
    public async Task APcThatHasGone_IsNoLongerBehind()
    {
        var update = await ConsoleAsync("0.4.0", "0.4.0");
        _connections.RaiseVersion("device-03", "0.3.0");
        _connections.RaiseEnded("device-03");

        Assert.Null(Message(update));
        Assert.Null(update.OutOfDate("device-03"));
    }

    [Fact]
    public async Task APcNewerThanTheConsole_IsNotBehind_AndIsSentNothing()
    {
        var update = await ConsoleAsync("0.3.0", "0.3.0");
        _connections.RaiseVersion("device-01", "0.4.0");

        Assert.Null(Message(update));
        Assert.Null(update.OutOfDate("device-01"));
        await update.UpdateAsync();
        Assert.Empty(_connections.UpdatesSent);
    }

    [Fact]
    public async Task Update_NotesThePcs_StartsTheConsolesSetupFirst_AndAsksToClose()
    {
        var update = await ConsoleAsync("0.3.0", "0.4.0");
        _connections.RaiseVersion("device-01", "0.3.0");
        _connections.RaiseVersion("device-02", "0.4.0");

        await update.UpdateAsync();

        Assert.Equal(["console.exe"], _self.Started);
        Assert.Equal(1, _exits);
        Assert.Empty(_connections.UpdatesSent);
        var pending = PendingUpdate.Read(PendingPath);
        Assert.Equal("0.4.0", pending?.Version);
        Assert.Equal(["device-01"], pending?.DeviceIds);
        Assert.True(update.IsUpdating("device-01"));
        Assert.False(update.IsUpdating("device-02"));
        Assert.Equal(("Updating this PC", "Windows will ask for permission. Promin opens again when it is done and goes on to the other PCs."),
            (Message(update)!.Title, Message(update)!.Text));
    }

    [Fact]
    public async Task WhileDownloading_ThePcsReadUpdating_AndTheBarSaysSo()
    {
        var download = new TaskCompletionSource<UpdateSet?>();
        _downloads.Get = (_, _) => download.Task;
        var update = await ConsoleAsync("0.3.0", "0.4.0");
        _connections.RaiseVersion("device-01", "0.3.0");

        var running = update.UpdateAsync();

        Assert.True(update.IsRunning);
        Assert.True(update.IsUpdating("device-01"));
        var message = Message(update)!;
        Assert.Equal(("Updating to Promin 0.4.0", "Downloading the update."), (message.Title, message.Text));
        Assert.False(message.ActionUpdates);
        download.SetResult(null);
        await running;
    }

    [Fact]
    public async Task RefusedPermission_ChangesNothing()
    {
        _self.Answer = false;
        var update = await ConsoleAsync("0.3.0", "0.4.0");
        _connections.RaiseVersion("device-01", "0.3.0");

        await update.UpdateAsync();

        Assert.Equal(0, _exits);
        Assert.False(File.Exists(PendingPath));
        Assert.False(update.IsRunning);
        Assert.False(update.IsUpdating("device-01"));
        Assert.Equal("Promin 0.4.0 is out", Message(update)!.Title);
    }

    [Fact]
    public async Task AFailedDownload_ShowsTheError_UntilAClickThatWorks()
    {
        _downloads.Get = (_, _) => Task.FromResult<UpdateSet?>(null);
        var update = await ConsoleAsync("0.3.0", "0.4.0");
        _connections.RaiseVersion("device-01", "0.3.0");

        await update.UpdateAsync();

        var message = Message(update)!;
        Assert.Equal(WallMessageKind.Error, message.Kind);
        Assert.Equal(("The update could not be downloaded", "Check this PC’s internet connection and try again."), (message.Title, message.Text));
        Assert.True(message.ActionUpdates);
        Assert.False(update.IsUpdating("device-01"));
        Assert.Empty(_self.Started);

        _downloads.Get = new FakeUpdateDownloads().Get;
        await update.UpdateAsync();
        Assert.Single(_self.Started);
    }

    [Fact]
    public async Task OnlyPcsBehind_GetTheCachedAgentSetup_WithoutAskingGitHub()
    {
        var cached = new UpdateFile("cached.exe", V("0.4.0"), [9]);
        _downloads.Cached[V("0.4.0")] = cached;
        var update = await ConsoleAsync("0.4.0", "0.4.0");
        _connections.RaiseVersion("device-03", "0.3.0");

        await update.UpdateAsync();

        Assert.Equal([("device-03", cached)], _connections.UpdatesSent);
        Assert.Empty(_downloads.Gets);
        Assert.Empty(_releases.AskedFor);
        Assert.Empty(_self.Started);
        Assert.Equal(("Updating to Promin 0.4.0", "0 of 1 PCs done."), (Message(update)!.Title, Message(update)!.Text));
    }

    [Fact]
    public async Task OnlyPcsBehind_WithNothingCached_DownloadsTheConsolesOwnVersion()
    {
        _releases.ByVersion[V("0.4.0")] = Published("0.4.0");
        var update = await ConsoleAsync("0.4.0", "0.4.0");
        _connections.RaiseVersion("device-03", "0.3.0");

        await update.UpdateAsync();

        Assert.Equal([V("0.4.0")], _releases.AskedFor);
        Assert.False(Assert.Single(_downloads.Gets).WithConsole);
        Assert.Single(_connections.UpdatesSent);
    }

    [Fact]
    public async Task APc_IsUpdated_WhenItComesBackOnTheTarget()
    {
        _downloads.Cached[V("0.4.0")] = new UpdateFile("cached.exe", V("0.4.0"), [9]);
        var update = await ConsoleAsync("0.4.0", "0.4.0");
        _connections.RaiseVersion("device-03", "0.3.0");
        _connections.RaiseVersion("device-04", "0.3.0");
        await update.UpdateAsync();

        _connections.RaiseEnded("device-03");
        Assert.True(update.IsUpdating("device-03"));
        _connections.RaiseVersion("device-03", "0.4.0");

        Assert.False(update.IsUpdating("device-03"));
        Assert.Equal("1 of 2 PCs done.", Message(update)!.Text);
        Assert.Equal(50, update.Progress);

        _connections.RaiseVersion("device-04", "0.4.0");
        Assert.False(update.IsRunning);
        Assert.Null(Message(update));
        Assert.Equal(0, update.Progress);
    }

    [Fact]
    public async Task APcThatComesBackOld_IsNotUpdated_AtOnce()
    {
        _downloads.Cached[V("0.4.0")] = new UpdateFile("cached.exe", V("0.4.0"), [9]);
        var update = await ConsoleAsync("0.4.0", "0.4.0");
        _connections.RaiseVersion("device-03", "0.3.0");
        await update.UpdateAsync();

        _connections.RaiseVersion("device-03", "0.3.0");

        Assert.False(update.IsRunning);
        Assert.Equal("PC-03 has an older Promin", Message(update)!.Title);
    }

    [Theory]
    [InlineData(UpdateOutcome.BadSignature)]
    [InlineData(UpdateOutcome.NotNewer)]
    [InlineData(UpdateOutcome.Failed)]
    [InlineData(null)]
    public async Task APcThatRefuses_OrWhoseConnectionEnds_IsNotUpdated(UpdateOutcome? answer)
    {
        _connections.UpdateAnswer = _ => answer;
        _downloads.Cached[V("0.4.0")] = new UpdateFile("cached.exe", V("0.4.0"), [9]);
        var update = await ConsoleAsync("0.4.0", "0.4.0");
        _connections.RaiseVersion("device-03", "0.3.0");

        await update.UpdateAsync();

        Assert.False(update.IsRunning);
        Assert.False(update.IsUpdating("device-03"));
        Assert.Equal("PC-03 has an older Promin", Message(update)!.Title);
    }

    [Fact]
    public async Task APcThatDoesNotComeBack_IsNotUpdated_AfterTheLimit()
    {
        _downloads.Cached[V("0.4.0")] = new UpdateFile("cached.exe", V("0.4.0"), [9]);
        var update = await ConsoleAsync("0.4.0", "0.4.0");
        _connections.RaiseVersion("device-03", "0.3.0");
        await update.UpdateAsync();
        _connections.RaiseEnded("device-03");

        _now += RoomUpdate.BackLimit - TimeSpan.FromSeconds(1);
        update.Tick();
        Assert.True(update.IsUpdating("device-03"));

        _now += TimeSpan.FromSeconds(1);
        update.Tick();
        Assert.False(update.IsUpdating("device-03"));
        Assert.False(update.IsRunning);
    }

    [Fact]
    public async Task Continue_SendsToEachNotedPc_AsItConnects()
    {
        var cached = new UpdateFile("cached.exe", V("0.4.0"), [9]);
        _downloads.Cached[V("0.4.0")] = cached;
        new PendingUpdate("0.4.0", ["device-01", "device-02"]).Write(PendingPath);
        var update = await ConsoleAsync("0.4.0", "0.4.0");

        update.Continue();

        Assert.False(File.Exists(PendingPath));
        Assert.True(update.IsUpdating("device-01"));
        Assert.True(update.IsUpdating("device-02"));
        _connections.RaiseVersion("device-01", "0.3.0");
        Assert.Equal([("device-01", cached)], _connections.UpdatesSent);
        _connections.RaiseVersion("device-02", "0.4.0");
        Assert.False(update.IsUpdating("device-02"));
        Assert.Single(_connections.UpdatesSent);

        _connections.RaiseVersion("device-01", "0.4.0");
        Assert.False(update.IsRunning);
    }

    [Fact]
    public async Task Continue_IgnoresAPendingFileForAnotherVersion()
    {
        _downloads.Cached[V("0.4.0")] = new UpdateFile("cached.exe", V("0.4.0"), [9]);
        new PendingUpdate("0.4.1", ["device-01"]).Write(PendingPath);
        var update = await ConsoleAsync("0.4.0", "0.4.0");

        update.Continue();

        Assert.False(File.Exists(PendingPath));
        Assert.False(update.IsRunning);
        Assert.False(update.IsUpdating("device-01"));
    }

    [Fact]
    public async Task ANotedPcThatNeverConnects_IsNotUpdated()
    {
        _downloads.Cached[V("0.4.0")] = new UpdateFile("cached.exe", V("0.4.0"), [9]);
        new PendingUpdate("0.4.0", ["device-01"]).Write(PendingPath);
        var update = await ConsoleAsync("0.4.0", "0.4.0");
        update.Continue();

        _now += RoomUpdate.ReconnectLimit;
        update.Tick();

        Assert.False(update.IsUpdating("device-01"));
        Assert.False(update.IsRunning);
    }

    [Fact]
    public async Task CheckAsync_WithANamedVersion_AsksForThatRelease()
    {
        _releases.ByVersion[V("0.4.0-beta.2")] = Published("0.4.0-beta.2");
        var update = new RoomUpdate(V("0.4.0-beta.1"), _releases, _downloads, _connections, _self, PendingPath, () => _now, a => a());

        await update.CheckAsync(V("0.4.0-beta.2"));

        Assert.Equal("Promin 0.4.0-beta.2 is out", Message(update)!.Title);
    }
}
