using System.Diagnostics;
using Promin.Master.Core.Wall;
using Promin.Protocol.Messages;
using Promin.Protocol.Updates;

namespace Promin.Master.Core.Updates;

/// <summary>
/// Updating the room from this console: it learns of a newer release once per start, and one click updates this
/// console first, then every connected agent PC that is behind. The wall shows what this says (<see cref="Message"/>,
/// <see cref="IsUpdating"/>, <see cref="OutOfDate"/>); nothing on the page decides anything.
/// </summary>
/// <remarks>
/// <para>
/// <b>The target</b> is the newer of GitHub's latest release and this console's own version. When the target is
/// newer, the click downloads both setups and runs this console's first, which asks Windows for permission. A refusal,
/// or a setup that fails before it installs, leaves this console open and changes nothing. The PCs that were connected
/// and behind are noted in the pending file; the console's setup closes this console, relaunches it with
/// <c>--continue-update</c>, and <see cref="Continue"/> then updates each noted PC as it connects.
/// When the console is the target already, the click sends its own version's agent setup to the PCs behind it.
/// </para>
/// <para>
/// <b>A PC</b> is updated when it connects again on the target. It is not updated when it refuses the setup, when its
/// connection ends before it answers, when it comes back on an older version, when it has not come back within
/// <see cref="BackLimit"/> of accepting, or, after a relaunch, when it has not connected within
/// <see cref="ReconnectLimit"/>. Nothing updates a PC without a click: one that was off stays behind until the next.
/// </para>
/// <para>
/// <b>Threads.</b> Every public member is called on the UI thread, and each answer is awaited there. The connections'
/// reports arrive on any thread and are marshalled through <c>runOnUiThread</c>.
/// </para>
/// </remarks>
public sealed class RoomUpdate
{
    /// <summary>How long a PC that accepted the setup has to connect again on the new version.</summary>
    public static readonly TimeSpan BackLimit = TimeSpan.FromMinutes(3);

    /// <summary>After this console's own update, how long a noted PC has to connect again.</summary>
    public static readonly TimeSpan ReconnectLimit = TimeSpan.FromMinutes(2);

    private readonly ReleaseVersion _own;
    private readonly IReleaseSource _releases;
    private readonly IUpdateDownloads _downloads;
    private readonly IRoomConnections _connections;
    private readonly ISelfUpdate _self;
    private readonly string _pendingPath;
    private readonly Func<DateTimeOffset> _now;
    private readonly Dictionary<string, ReleaseVersion> _versions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Pc> _run = new(StringComparer.Ordinal);
    private Release? _latest;
    private Phase _phase;
    private bool _failed;
    private ReleaseVersion _runTarget;
    private UpdateFile? _runFile;

    public RoomUpdate(
        ReleaseVersion own,
        IReleaseSource releases,
        IUpdateDownloads downloads,
        IRoomConnections connections,
        ISelfUpdate self,
        string pendingPath,
        Func<DateTimeOffset> now,
        Action<Action> runOnUiThread)
    {
        ArgumentNullException.ThrowIfNull(own);
        ArgumentNullException.ThrowIfNull(releases);
        ArgumentNullException.ThrowIfNull(downloads);
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(self);
        ArgumentNullException.ThrowIfNull(pendingPath);
        ArgumentNullException.ThrowIfNull(now);
        ArgumentNullException.ThrowIfNull(runOnUiThread);

        _own = own;
        _releases = releases;
        _downloads = downloads;
        _connections = connections;
        _self = self;
        _pendingPath = pendingPath;
        _now = now;
        _runTarget = own;
        connections.VersionReported += (id, version) => runOnUiThread(() => OnVersion(id, version));
        connections.Ended += id => runOnUiThread(() => OnEnded(id));
    }

    private enum Phase { Idle, Downloading, StartingSelf, UpdatingPcs }

    private enum Step { WaitingToConnect, Sending, WaitingToComeBack, Done }

    /// <summary>Raised on the UI thread whenever anything this says has changed.</summary>
    public event Action? Changed;

    /// <summary>True from the click until every PC being updated is updated or not, or the update is given up.</summary>
    public bool IsRunning => _phase != Phase.Idle;

    /// <summary>0 to 100 over the PCs being updated, each counting once it is updated or not; 0 when no PC is being updated.</summary>
    public double Progress => _phase == Phase.UpdatingPcs && _run.Count > 0
        ? 100.0 * _run.Values.Count(p => p.Step == Step.Done) / _run.Count
        : 0;

    private ReleaseVersion Target => _latest is { } latest && latest.Version > _own ? latest.Version : _own;

    /// <summary>Asks for the latest release, or with the Debug-only test option the release of <paramref name="named"/>. Once, at start.</summary>
    public async Task CheckAsync(ReleaseVersion? named)
    {
        _latest = named is null
            ? await _releases.LatestAsync(CancellationToken.None)
            : await _releases.ByVersionAsync(named, CancellationToken.None);
        Changed?.Invoke();
    }

    /// <summary>
    /// After this console's own setup relaunched it: reads and deletes the pending file and, when it is for this
    /// console's version, updates the PCs it names as each connects.
    /// </summary>
    public void Continue()
    {
        var pending = PendingUpdate.Read(_pendingPath);
        PendingUpdate.Delete(_pendingPath);
        if (pending is null || !ReleaseVersion.TryParse(pending.Version, out var version) || version != _own)
        {
            return;
        }

        _runFile = _downloads.CachedAgent(_own);
        if (_runFile is null)
        {
            Trace.TraceWarning("The agent setup of {0} is not in the cache, so the noted PCs were not updated.", _own);
            return;
        }

        _runTarget = _own;
        _phase = Phase.UpdatingPcs;
        var until = _now() + ReconnectLimit;
        foreach (var id in pending.DeviceIds)
        {
            _run[id] = new Pc { Step = Step.WaitingToConnect, Until = until };
        }

        foreach (var id in pending.DeviceIds)
        {
            if (_versions.TryGetValue(id, out var reported))
            {
                Arrived(id, reported);
            }
        }

        Finish();
        Changed?.Invoke();
    }

    /// <summary>True while the PC is being updated, from the click until it is updated or not.</summary>
    public bool IsUpdating(string deviceId) => _run.TryGetValue(deviceId, out var pc) && pc.Step != Step.Done;

    /// <summary>The version of a connected PC whose agent is older than this console; otherwise null.</summary>
    public ReleaseVersion? OutOfDate(string deviceId) =>
        _versions.TryGetValue(deviceId, out var version) && version < _own ? version : null;

    /// <summary>What the bar above the wall says about updates, with the PCs named by <paramref name="nameOf"/>; null for nothing.</summary>
    public WallMessage? Message(Func<string, string?> nameOf)
    {
        ArgumentNullException.ThrowIfNull(nameOf);
        switch (_phase)
        {
            case Phase.Downloading:
                return new(WallMessageKind.Info, RunTitle, Strings.Update_DownloadingText);
            case Phase.StartingSelf:
                return new(WallMessageKind.Info, Strings.Update_SelfTitle, Strings.Update_SelfText);
            case Phase.UpdatingPcs:
                return new(
                    WallMessageKind.Info,
                    RunTitle,
                    Phrase.Format(Strings.Update_PcsText, ("done", _run.Values.Count(p => p.Step == Step.Done)), ("count", _run.Count)));
        }

        if (_failed)
        {
            return new(WallMessageKind.Error, Strings.Update_FailedTitle, Strings.Update_FailedText, Strings.Update_Button, ActionUpdates: true);
        }

        var own = _own.ToString();
        if (Target > _own)
        {
            var count = _versions.Values.Count(v => v < Target);
            return new(
                WallMessageKind.Info,
                Phrase.Format(Strings.Update_OutTitle, ("version", Target.ToString())),
                Phrase.Format(Strings.Update_OutText, ("count", count), ("own", own)),
                Strings.Update_Button,
                ActionUpdates: true);
        }

        var behind = _versions
            .Where(p => p.Value < _own && nameOf(p.Key) is not null)
            .Select(p => (Name: nameOf(p.Key)!, Version: p.Value))
            .OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        return behind switch
        {
            [] => null,
            [var one] => Offer(
                Phrase.Format(Strings.Update_BehindTitleOne, ("name", one.Name)),
                Phrase.Format(Strings.Update_BehindTextOne, ("own", own), ("name", one.Name), ("theirs", one.Version.ToString()))),
            [var first, var second] => Offer(
                Phrase.Format(Strings.Update_BehindTitleTwo, ("first", first.Name), ("second", second.Name)),
                Phrase.Format(Strings.Update_BehindTextMany, ("own", own))),
            [var first, var second, .. var rest] => Offer(
                Phrase.Format(Strings.Update_BehindTitleMany, ("first", first.Name), ("second", second.Name), ("count", rest.Count)),
                Phrase.Format(Strings.Update_BehindTextMany, ("own", own))),
        };

        static WallMessage Offer(string title, string text) =>
            new(WallMessageKind.Info, title, text, Strings.Update_Button, ActionUpdates: true);
    }

    /// <summary>The click: updates this console first when it is behind, otherwise the connected PCs behind it. Does nothing while running.</summary>
    public async Task UpdateAsync()
    {
        if (IsRunning)
        {
            return;
        }

        var target = Target;
        var noted = _versions.Where(p => p.Value < target).Select(p => p.Key).ToList();
        _failed = false;
        _runTarget = target;
        foreach (var id in noted)
        {
            _run[id] = new Pc { Step = Step.WaitingToConnect };
        }

        _phase = Phase.Downloading;
        Changed?.Invoke();

        var files = await GetFilesAsync(target);
        if (files is null)
        {
            _failed = true;
            EndRun();
            return;
        }

        if (target > _own)
        {
            await UpdateSelfAsync(files.Console!, target, noted);
            return;
        }

        _runFile = files.Agent;
        _phase = Phase.UpdatingPcs;
        Changed?.Invoke();
        await Task.WhenAll(noted.Select(SendAsync));
        Finish();
    }

    /// <summary>Gives up on PCs past their limit. Called once a second.</summary>
    public void Tick()
    {
        var now = _now();
        var changed = false;
        foreach (var pc in _run.Values)
        {
            if (pc.Step is Step.WaitingToConnect or Step.WaitingToComeBack && pc.Until is { } until && now >= until)
            {
                pc.Step = Step.Done;
                changed = true;
            }
        }

        if (changed)
        {
            Finish();
            Changed?.Invoke();
        }
    }

    private string RunTitle => Phrase.Format(Strings.Update_RunTitle, ("version", _runTarget.ToString()));

    private async Task<UpdateSet?> GetFilesAsync(ReleaseVersion target)
    {
        if (target > _own)
        {
            return await _downloads.GetAsync(_latest!, withConsole: true, CancellationToken.None);
        }

        if (_downloads.CachedAgent(_own) is { } cached)
        {
            return new UpdateSet(cached, null);
        }

        return await _releases.ByVersionAsync(_own, CancellationToken.None) is { } release
            ? await _downloads.GetAsync(release, withConsole: false, CancellationToken.None)
            : null;
    }

    private async Task UpdateSelfAsync(UpdateFile console, ReleaseVersion target, List<string> noted)
    {
        try
        {
            new PendingUpdate(target.ToString(), noted).Write(_pendingPath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // This console still updates; the PCs it noted show as behind when it opens again, for one more click.
            Trace.TraceWarning("The PCs to update after this PC's own update could not be noted: {0}", e.Message);
        }

        _phase = Phase.StartingSelf;
        Changed?.Invoke();
        await _self.RunAsync(console.SetupPath);

        // Still open, so this PC was not updated.
        PendingUpdate.Delete(_pendingPath);
        EndRun();
    }

    private async Task SendAsync(string id)
    {
        if (!_run.TryGetValue(id, out var pc) || _runFile is null)
        {
            return;
        }

        pc.Step = Step.Sending;
        var answer = await _connections.UpdateAsync(id, _runFile);
        if (answer == UpdateOutcome.Accepted)
        {
            pc.Step = Step.WaitingToComeBack;
            pc.Until = _now() + BackLimit;
        }
        else
        {
            Trace.TraceInformation("PC {0} was not updated: {1}", id, answer?.ToString() ?? "its connection ended");
            pc.Step = Step.Done;
        }

        Finish();
        Changed?.Invoke();
    }

    private void OnVersion(string id, ReleaseVersion version)
    {
        _versions[id] = version;
        if (_phase == Phase.UpdatingPcs)
        {
            Arrived(id, version);
            Finish();
        }

        Changed?.Invoke();
    }

    private void Arrived(string id, ReleaseVersion version)
    {
        if (!_run.TryGetValue(id, out var pc))
        {
            return;
        }

        switch (pc.Step)
        {
            case Step.WaitingToConnect when version < _runTarget:
                _ = SendAsync(id);
                break;
            case Step.WaitingToConnect:
            case Step.WaitingToComeBack:
                pc.Step = Step.Done;
                break;
        }
    }

    private void OnEnded(string id)
    {
        if (_versions.Remove(id))
        {
            Changed?.Invoke();
        }
    }

    // Once every PC is updated or not, the run is over: the bar goes back to naming whatever is still behind.
    private void Finish()
    {
        if (_phase == Phase.UpdatingPcs && _run.Values.All(p => p.Step == Step.Done))
        {
            EndRun();
        }
    }

    private void EndRun()
    {
        _phase = Phase.Idle;
        _run.Clear();
        _runFile = null;
        Changed?.Invoke();
    }

    private sealed class Pc
    {
        public Step Step { get; set; }

        public DateTimeOffset? Until { get; set; }
    }
}
