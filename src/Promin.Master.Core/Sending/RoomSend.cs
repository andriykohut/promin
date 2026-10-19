using System.ComponentModel;
using Promin.Master.Core.Wall;
using Promin.Protocol.Messages;
using Promin.Protocol.Sending;

namespace Promin.Master.Core.Sending;

/// <summary>
/// The room's send, running or last run: it sends a package to every chosen PC at once, puts each PC's progress and
/// outcome on its tile, and says what the send strip shows. The page binds the strip to it and calls into it; nothing
/// on the page decides anything.
/// </summary>
/// <remarks>
/// <b>Threads.</b> Every public member is called on the UI thread, and each PC's answer is awaited there. The bytes
/// written to a PC arrive on any thread and are marshalled through <c>runOnUiThread</c>, only when that PC's whole
/// percent changes, so its tile and <see cref="Progress"/> always agree.
///
/// <b>Marks.</b> A tick clears ten seconds after the last PC finished, on <see cref="Tick"/>; a send every PC got
/// closes its strip then too. A "not sent" mark, and any other strip, stays until the next send; the strip also
/// closes with <see cref="CloseStrip"/>.
/// </remarks>
public sealed class RoomSend(IFileSender sender, Func<string, PcTile?> tileOf, Func<DateTimeOffset> now, Action<Action> runOnUiThread)
    : INotifyPropertyChanged
{
    private static readonly TimeSpan TicksShow = TimeSpan.FromSeconds(10);

    private static readonly string[] Shown =
    [
        nameof(IsRunning), nameof(IsStripOpen), nameof(StripKind), nameof(StripTitle), nameof(StripText),
        nameof(Progress), nameof(StripActionLabel), nameof(HasStripAction), nameof(IsStripClosable),
    ];

    private List<Pc> _pcs = [];
    private SendPackage? _package;
    private bool _open;
    private CancellationTokenSource _stop = new();
    private bool _running;
    private bool _stripOpen;

    // The path, within the send, of a file the console could not read; set when that stopped the send.
    private string? _unreadable;

    // When the last PC finished; null while running, and once its ticks are cleared.
    private DateTimeOffset? _endedAt;

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool IsRunning => _running;

    public bool IsStripOpen => _stripOpen;

    public SendStripKind StripKind =>
        _running ? SendStripKind.Sending
        : _unreadable is not null ? SendStripKind.SourceFailed
        : _pcs.All(p => p.Mark == TileSendMark.Done) ? SendStripKind.AllSent
        : SendStripKind.SomeMissed;

    public string StripTitle => _package is null ? string.Empty : StripKind switch
    {
        SendStripKind.Sending => Phrase.Format(Strings.SendStrip_Sending, ("what", What)),
        SendStripKind.SomeMissed when Have == 0 => Phrase.Format(Strings.SendStrip_NotSent, ("what", What), ("items", Items)),
        SendStripKind.SomeMissed => SentTo,

        // "Sent to all 1 PCs" would read wrong.
        SendStripKind.AllSent when _pcs.Count == 1 => SentTo,
        SendStripKind.AllSent => Phrase.Format(Strings.SendStrip_AllSent, ("what", What), ("count", _pcs.Count)),
        _ => Strings.SendStrip_SourceTitle,
    };

    public string StripText => _package is null ? string.Empty : StripKind switch
    {
        // "0 of 9 already have it" would read wrong; the line waits for the first PC.
        SendStripKind.Sending when Have > 0 => Phrase.Format(Strings.SendStrip_Have, ("have", Have), ("count", _pcs.Count), ("items", Items)),
        SendStripKind.SomeMissed => Missed switch
        {
            [var one] when SendText.Why(one.Reason) is { } why =>
                Phrase.Format(Strings.SendStrip_MissedOneWhy, ("name", one.Target.Name), ("items", Items), ("reason", why)),
            [var one] => Phrase.Format(Strings.SendStrip_MissedOne, ("name", one.Target.Name), ("items", Items)),
            var several => Phrase.Format(Strings.SendStrip_MissedMany, ("count", several.Count), ("items", Items)),
        },
        SendStripKind.SourceFailed => Phrase.Format(Strings.SendStrip_SourceText, ("file", _unreadable!)),
        _ => string.Empty,
    };

    /// <summary>
    /// 0 to 100 over the bytes of every PC the send went to. A PC that has finished, whatever its outcome, counts in
    /// full, so the bar reaches the end with the last PC.
    /// </summary>
    public double Progress
    {
        get
        {
            var sentTo = _pcs.Where(p => p.Target.Online).ToList();
            if (_package is null || sentTo.Count == 0)
            {
                return 0;
            }

            // A send of only empty files and folders has no bytes to count: each PC counts as it finishes.
            var total = Math.Max(_package.TotalBytes, 1);
            var bytes = sentTo.Sum(p => p.Mark == TileSendMark.Sending ? p.Bytes : total);
            return 100.0 * bytes / (total * sentTo.Count);
        }
    }

    /// <summary>Stop sending while sending; Send again after a send some PCs missed; otherwise empty.</summary>
    public string StripActionLabel => _package is null ? string.Empty : StripKind switch
    {
        SendStripKind.Sending => Strings.SendStrip_Stop,
        SendStripKind.SomeMissed => Missed is [var one]
            ? Phrase.Format(Strings.SendStrip_AgainOne, ("name", one.Target.Name))
            : Phrase.Format(Strings.SendStrip_AgainMany, ("count", Missed.Count)),
        _ => string.Empty,
    };

    public bool HasStripAction => StripActionLabel.Length > 0;

    public bool IsStripClosable => !_running;

    private string What => _package!.Picked.Count == 1
        ? Path.GetFileName(Path.TrimEndingDirectorySeparator(_package.Picked[0].Path))
        : Phrase.Format(Strings.Send_Items, ("count", _package.Picked.Count));

    private int Items => _package!.Picked.Count;

    private int Have => _pcs.Count(p => p.Mark == TileSendMark.Done);

    private List<Pc> Missed => _pcs.Where(p => p.Mark == TileSendMark.NotSent).ToList();

    private string SentTo => Phrase.Format(Strings.SendStrip_SentTo, ("what", What), ("count", Have));

    /// <summary>
    /// Sends <paramref name="package"/> to each chosen PC that is online; one that is not reads "not sent: offline" at
    /// once. The last send's marks go. Ignored while a send is running, and for no PCs.
    /// </summary>
    public void Start(SendPackage package, bool open, IReadOnlyList<SendTarget> chosen)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(chosen);
        if (_running || chosen.Count == 0)
        {
            return;
        }

        foreach (var pc in _pcs)
        {
            Show(pc.Target.Member.DeviceId, TileSendMark.None, 0, string.Empty);
        }

        _package = package;
        _open = open;
        _unreadable = null;
        _endedAt = null;
        _stop = new CancellationTokenSource();
        _running = true;
        _stripOpen = true;

        // Every mark is set before the first send starts, so a send that ends at once cannot end the whole send early.
        _pcs = chosen.Select(t => new Pc(t)).ToList();
        foreach (var pc in _pcs)
        {
            Show(pc);
        }

        foreach (var pc in _pcs.Where(p => p.Mark == TileSendMark.Sending))
        {
            _ = SendAsync(pc, package, open, _stop);
        }

        EndIfFinished();
        Changed();
    }

    /// <summary>
    /// While sending, stops the send to every PC still receiving; they read "not sent: stopped". After a send some PCs
    /// missed, sends the same items with the same open setting to those PCs only, each tried whether or not it was online.
    /// </summary>
    public void StripAction()
    {
        if (_running)
        {
            _stop.Cancel();
        }
        else if (StripKind == SendStripKind.SomeMissed)
        {
            Start(_package!, _open, Missed.Select(p => p.Target with { Online = true }).ToList());
        }
    }

    /// <summary>Closes the strip. Ignored while sending.</summary>
    public void CloseStrip()
    {
        if (!_running)
        {
            _stripOpen = false;
            Changed();
        }
    }

    /// <summary>Once a second from the page's timer: clears the ticks ten seconds after the last PC finished.</summary>
    public void Tick()
    {
        if (_endedAt is not { } endedAt || now() - endedAt < TicksShow)
        {
            return;
        }

        _endedAt = null;
        foreach (var pc in _pcs.Where(p => p.Mark == TileSendMark.Done))
        {
            Show(pc.Target.Member.DeviceId, TileSendMark.None, 0, string.Empty);
        }

        if (StripKind == SendStripKind.AllSent)
        {
            _stripOpen = false;
        }

        Changed();
    }

    /// <summary>One PC's send, from start to its tile's mark. Never throws.</summary>
    private async Task SendAsync(Pc pc, SendPackage package, bool open, CancellationTokenSource stop)
    {
        var shownPercent = 0;
        NotSentReason? reason;
        try
        {
            var outcome = await sender.SendAsync(pc.Target.Member, package, open, Written, stop.Token).ConfigureAwait(true);
            reason = outcome switch
            {
                SendOutcome.Sent => null,
                null => NotSentReason.Offline,
                SendOutcome.NotEnoughSpace => NotSentReason.NotEnoughSpace,
                SendOutcome.InUse => NotSentReason.InUse,
                _ => NotSentReason.Other,
            };
        }
        catch (Exception) when (stop.IsCancellationRequested)
        {
            // However the stream ends once the send is stopped, it was stopped.
            reason = NotSentReason.Stopped;
        }
        catch (SendSourceException e)
        {
            // What the console cannot read, no PC gets.
            _unreadable ??= e.Path;
            stop.Cancel();
            reason = NotSentReason.Stopped;
        }
        catch (Exception)
        {
            reason = NotSentReason.ConnectionLost;
        }

        pc.Mark = reason is null ? TileSendMark.Done : TileSendMark.NotSent;
        pc.Reason = reason ?? pc.Reason;
        Show(pc);
        EndIfFinished();
        Changed();

        void Written(long bytes)
        {
            var percent = Percent(bytes, package.TotalBytes);
            if (percent != shownPercent)
            {
                shownPercent = percent;
                runOnUiThread(() => Progressed(pc, bytes));
            }
        }
    }

    private void Progressed(Pc pc, long bytes)
    {
        // A count queued behind the PC's answer is stale.
        if (pc.Mark != TileSendMark.Sending)
        {
            return;
        }

        pc.Bytes = bytes;
        Show(pc);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Progress)));
    }

    private void EndIfFinished()
    {
        if (_running && _pcs.All(p => p.Mark != TileSendMark.Sending))
        {
            _running = false;
            _endedAt = now();
        }
    }

    private void Show(Pc pc) => Show(
        pc.Target.Member.DeviceId,
        pc.Mark,
        Percent(pc.Bytes, _package!.TotalBytes),
        pc.Mark == TileSendMark.NotSent ? SendText.NotSent(pc.Reason) : string.Empty);

    // The tile is looked up each time: the PC may have been removed from the room meanwhile.
    private void Show(string deviceId, TileSendMark mark, int percent, string detail)
    {
        if (tileOf(deviceId) is { } tile)
        {
            tile.SendMark = mark;
            tile.SendPercent = percent;
            tile.SendDetail = detail;
        }
    }

    private static int Percent(long bytes, long total) => total == 0 ? 0 : (int)(bytes * 100 / total);

    private void Changed()
    {
        foreach (var name in Shown)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    private sealed class Pc(SendTarget target)
    {
        public SendTarget Target { get; } = target;

        public TileSendMark Mark { get; set; } = target.Online ? TileSendMark.Sending : TileSendMark.NotSent;

        /// <summary>Why it did not get the send, while <see cref="Mark"/> is <see cref="TileSendMark.NotSent"/>.</summary>
        public NotSentReason Reason { get; set; } = NotSentReason.Offline;

        /// <summary>Bytes written to it, as last marshalled to the UI thread.</summary>
        public long Bytes { get; set; }
    }
}
