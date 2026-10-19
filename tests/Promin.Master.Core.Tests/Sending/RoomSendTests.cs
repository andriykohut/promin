using Promin.Master.Core.Rooms;
using Promin.Master.Core.Sending;
using Promin.Master.Core.Tests.TestDoubles;
using Promin.Master.Core.Wall;
using Promin.Protocol.Messages;
using Promin.Protocol.Sending;

namespace Promin.Master.Core.Tests.Sending;

public sealed class RoomSendTests
{
    private static readonly SendPackage Quiz = FakeFileSender.Package(("quiz-chapter-3.html", 1000));

    private readonly FakeFileSender _sender = new();
    private readonly Dictionary<string, PcTile> _tiles;
    private readonly RoomSend _send;
    private DateTimeOffset _now = new(2026, 10, 18, 10, 0, 0, TimeSpan.Zero);

    public RoomSendTests()
    {
        _tiles = Enumerable.Range(1, 9).Select(Member).ToDictionary(m => m.DeviceId, m => new PcTile(m, TimeSpan.FromSeconds(10)));
        _send = Build(a => a());
    }

    private static string Id(int n) => $"device-{n:00}";

    private static RoomMember Member(int n) => new(Id(n), $"PC-{n:00}", $"192.168.1.{20 + n}", 17843, new DateOnly(2026, 9, 22), true);

    private static SendTarget Pc(int n, bool online = true) => new(Member(n), $"PC-{n:00}", online);

    /// <summary>PC-01 to PC-09, PC-07 offline.</summary>
    private static List<SendTarget> NineWithSevenOffline() => Enumerable.Range(1, 9).Select(n => Pc(n, online: n != 7)).ToList();

    private static List<SendTarget> EightOnline() => [.. Enumerable.Range(1, 8).Select(n => Pc(n))];

    private RoomSend Build(Action<Action> runOnUiThread) => new(_sender, id => _tiles.GetValueOrDefault(id), () => _now, runOnUiThread);

    private PcTile Tile(int n) => _tiles[Id(n)];

    private FakeFileSender.Send SendTo(int n) => _sender.To(Id(n));

    private void Answer(SendOutcome? outcome, params int[] pcs)
    {
        foreach (var n in pcs)
        {
            SendTo(n).Answer.SetResult(outcome);
        }
    }

    [Fact]
    public void Start_SendsToEveryOnlineChosenPc_AndMarksOfflineOnesNotSent()
    {
        _send.Start(Quiz, open: false, NineWithSevenOffline());

        Assert.Equal(TileSendMark.NotSent, Tile(7).SendMark);
        Assert.Equal("Not sent: offline.", Tile(7).SendDetail);
        Assert.All([1, 2, 3, 4, 5, 6, 8, 9], n => Assert.Equal(TileSendMark.Sending, Tile(n).SendMark));
        Assert.All([1, 2, 3, 4, 5, 6, 8, 9], n => Assert.Equal(string.Empty, Tile(n).SendDetail));
        Assert.Equal([1, 2, 3, 4, 5, 6, 8, 9], _sender.Sends.Select(s => int.Parse(s.Pc.DeviceId[^2..])));
        Assert.All(_sender.Sends, s => Assert.Same(Quiz, s.Package));
        Assert.True(_send.IsRunning);
        Assert.True(_send.IsStripOpen);
    }

    [Fact]
    public void Progress_IsBytesOverTheTotal_PerTileAndOverall()
    {
        _send.Start(Quiz, open: false, NineWithSevenOffline());

        SendTo(1).Written(500);

        Assert.Equal(50, Tile(1).SendPercent);
        Assert.Equal(0, Tile(2).SendPercent);
        Assert.Equal(6.25, _send.Progress);

        // Within the same whole percent nothing moves, the tile or the bar.
        SendTo(1).Written(505);

        Assert.Equal(50, Tile(1).SendPercent);
        Assert.Equal(6.25, _send.Progress);
    }

    [Fact]
    public void Progress_ArrivesThroughTheUiDispatcher()
    {
        var queue = new Queue<Action>();
        var send = Build(queue.Enqueue);
        send.Start(Quiz, open: false, EightOnline());

        SendTo(3).Written(640);
        Assert.Equal(0, Tile(3).SendPercent);

        while (queue.TryDequeue(out var action))
        {
            action();
        }

        Assert.Equal(64, Tile(3).SendPercent);
        Assert.Equal(8, send.Progress);
    }

    [Fact]
    public void ACountQueuedBehindItsPcsAnswer_DoesNotBringBackAClearedTick()
    {
        var queue = new Queue<Action>();
        var send = Build(queue.Enqueue);
        send.Start(Quiz, open: false, [Pc(3)]);

        SendTo(3).Written(640);
        Answer(SendOutcome.Sent, 3);
        _now += TimeSpan.FromSeconds(10);
        send.Tick();
        while (queue.TryDequeue(out var action))
        {
            action();
        }

        Assert.Equal(TileSendMark.None, Tile(3).SendMark);
        Assert.Equal(100, send.Progress);
    }

    [Fact]
    public void AFinishedPc_CountsInFull_TowardsTheBar()
    {
        _send.Start(Quiz, open: false, EightOnline());

        SendTo(1).Written(1000);
        Answer(SendOutcome.Sent, 1);
        SendTo(2).Written(300);
        SendTo(2).Answer.SetException(new IOException("gone"));

        Assert.Equal(25, _send.Progress);
    }

    [Fact]
    public void TheSendingStrip_SaysWhatAndHowManyHaveIt()
    {
        _send.Start(Quiz, open: false, NineWithSevenOffline());

        Answer(SendOutcome.Sent, 1, 2, 3, 4, 5);

        Assert.Equal(SendStripKind.Sending, _send.StripKind);
        Assert.Equal("Sending quiz-chapter-3.html", _send.StripTitle);
        Assert.Equal("5 of 9 have it.", _send.StripText);
        Assert.Equal("Stop sending", _send.StripActionLabel);
        Assert.True(_send.HasStripAction);
        Assert.False(_send.IsStripClosable);

        _send.CloseStrip();

        Assert.True(_send.IsStripOpen);
    }

    [Fact]
    public void SeveralItems_AreCounted_AndCalledThem()
    {
        var package = FakeFileSender.Package(("a.txt", 10), ("b.txt", 10), ("c.txt", 10));
        _send.Start(package, open: false, EightOnline());

        Assert.Equal("Sending 3 items", _send.StripTitle);
        Assert.Equal("0 of 8 have them.", _send.StripText);

        Answer(SendOutcome.Sent, 1, 2, 3, 4, 5, 6, 7, 8);

        Assert.Equal("3 items sent to all 8 PCs.", _send.StripTitle);
    }

    [Fact]
    public void AllSent_ShowsSuccess_ThenTicksAndStripClearTenSecondsAfterTheEnd()
    {
        _send.Start(Quiz, open: false, EightOnline());
        Answer(SendOutcome.Sent, 1, 2, 3, 4, 5, 6, 7);
        _now += TimeSpan.FromSeconds(5);
        Answer(SendOutcome.Sent, 8);

        Assert.False(_send.IsRunning);
        Assert.Equal(SendStripKind.AllSent, _send.StripKind);
        Assert.Equal("quiz-chapter-3.html sent to all 8 PCs.", _send.StripTitle);
        Assert.Equal(string.Empty, _send.StripText);
        Assert.False(_send.HasStripAction);
        Assert.Equal(string.Empty, _send.StripActionLabel);
        Assert.True(_send.IsStripClosable);
        Assert.All([1, 2, 3, 4, 5, 6, 7, 8], n => Assert.Equal(TileSendMark.Done, Tile(n).SendMark));

        _now += TimeSpan.FromSeconds(9);
        _send.Tick();

        Assert.True(_send.IsStripOpen);
        Assert.Equal(TileSendMark.Done, Tile(1).SendMark);

        _now += TimeSpan.FromSeconds(1);
        _send.Tick();

        Assert.False(_send.IsStripOpen);
        Assert.All([1, 2, 3, 4, 5, 6, 7, 8], n => Assert.Equal(TileSendMark.None, Tile(n).SendMark));
    }

    [Fact]
    public void AllSent_ToTheOnlyPc_SaysOnePc()
    {
        _send.Start(Quiz, open: false, [Pc(1)]);
        Answer(SendOutcome.Sent, 1);

        Assert.Equal("quiz-chapter-3.html sent to 1 PC.", _send.StripTitle);
    }

    [Fact]
    public void SomeMissed_ShowsAWarning_NamingThePc_WithSendAgain()
    {
        _send.Start(Quiz, open: false, NineWithSevenOffline());
        Answer(SendOutcome.Sent, 1, 2, 3, 4, 5, 6, 8, 9);

        Assert.Equal(SendStripKind.SomeMissed, _send.StripKind);
        Assert.Equal("quiz-chapter-3.html sent to 8 PCs.", _send.StripTitle);
        Assert.Equal("PC-07 did not get it: it is offline.", _send.StripText);
        Assert.Equal("Send again to PC-07", _send.StripActionLabel);
        Assert.True(_send.HasStripAction);
        Assert.True(_send.IsStripClosable);

        _now += TimeSpan.FromMinutes(1);
        _send.Tick();

        Assert.True(_send.IsStripOpen);

        _send.CloseStrip();

        Assert.False(_send.IsStripOpen);
    }

    [Theory]
    [InlineData(NotSentReason.Offline, "PC-03 did not get it: it is offline.")]
    [InlineData(NotSentReason.ConnectionLost, "PC-03 did not get it: the connection was lost.")]
    [InlineData(NotSentReason.NotEnoughSpace, "PC-03 did not get it: there is not enough space.")]
    [InlineData(NotSentReason.InUse, "PC-03 did not get it: the old copy is in use.")]
    [InlineData(NotSentReason.Stopped, "PC-03 did not get it: the send was stopped.")]
    [InlineData(NotSentReason.Other, "PC-03 did not get it.")]
    public void OneMissedPc_IsNamed_WithWhyItMissed(NotSentReason reason, string expected)
    {
        _send.Start(Quiz, open: false, [.. EightOnline().Select(t => t.Name == "PC-03" ? t with { Online = reason != NotSentReason.Offline } : t)]);
        Answer(SendOutcome.Sent, 1, 2, 4, 5, 6, 7, 8);

        switch (reason)
        {
            case NotSentReason.ConnectionLost:
                SendTo(3).Answer.SetException(new IOException("gone"));
                break;
            case NotSentReason.NotEnoughSpace:
                Answer(SendOutcome.NotEnoughSpace, 3);
                break;
            case NotSentReason.InUse:
                Answer(SendOutcome.InUse, 3);
                break;
            case NotSentReason.Stopped:
                _send.StripAction();
                break;
            case NotSentReason.Other:
                Answer(SendOutcome.Failed, 3);
                break;
        }

        Assert.Equal(SendStripKind.SomeMissed, _send.StripKind);
        Assert.Equal(expected, _send.StripText);
    }

    [Fact]
    public void NoPcGotIt_SaysItWasNotSent()
    {
        _send.Start(Quiz, open: false, NineWithSevenOffline());

        _send.StripAction();

        Assert.Equal("quiz-chapter-3.html was not sent.", _send.StripTitle);
        Assert.Equal("9 PCs did not get it.", _send.StripText);
    }

    [Fact]
    public void NoPcGotSeveralItems_SaysTheyWereNotSent()
    {
        _send.Start(FakeFileSender.Package(("a.txt", 10), ("b.txt", 10), ("c.txt", 10)), open: false, NineWithSevenOffline());

        _send.StripAction();

        Assert.Equal("3 items were not sent.", _send.StripTitle);
        Assert.Equal("9 PCs did not get them.", _send.StripText);
    }

    [Fact]
    public void SomeMissed_ByMoreThanOnePc_CountsThem()
    {
        _send.Start(Quiz, open: false, NineWithSevenOffline());
        Answer(SendOutcome.Sent, 1, 2, 3, 4, 5, 6, 8);
        Answer(SendOutcome.Failed, 9);

        Assert.Equal("quiz-chapter-3.html sent to 7 PCs.", _send.StripTitle);
        Assert.Equal("2 PCs did not get it.", _send.StripText);
        Assert.Equal("Send again to 2 PCs", _send.StripActionLabel);
    }

    [Fact]
    public void ANotSentMark_StaysUntilTheNextSend_EvenWhenThePcComesBack()
    {
        _send.Start(Quiz, open: false, NineWithSevenOffline());
        Answer(SendOutcome.Sent, 1, 2, 3, 4, 5, 6, 8, 9);

        _now += TimeSpan.FromMinutes(5);
        _send.Tick();

        Assert.Equal(TileSendMark.NotSent, Tile(7).SendMark);
        Assert.Equal("Not sent: offline.", Tile(7).SendDetail);
        Assert.Equal(TileSendMark.None, Tile(1).SendMark);

        // Back online, PC-07 is in the next send's list; PC-09 is not, and has no mark either.
        _send.Start(Quiz, open: false, [Pc(7)]);

        Assert.Equal(TileSendMark.Sending, Tile(7).SendMark);
        Assert.Equal(string.Empty, Tile(7).SendDetail);
        Assert.Equal(TileSendMark.None, Tile(9).SendMark);
    }

    [Fact]
    public void TheNextSend_ClearsTheLastOnesMarks_OnPcsItDoesNotGoTo()
    {
        _send.Start(Quiz, open: false, NineWithSevenOffline());
        Answer(SendOutcome.Sent, 1, 2, 3, 4, 5, 6, 8, 9);

        _send.Start(Quiz, open: false, [Pc(1)]);

        Assert.Equal(TileSendMark.None, Tile(7).SendMark);
        Assert.Equal(string.Empty, Tile(7).SendDetail);
        Assert.Equal(TileSendMark.None, Tile(2).SendMark);
    }

    [Fact]
    public void SendAgain_SendsTheSameItemsAndOpenSetting_ToTheMissedPcsOnly()
    {
        _send.Start(Quiz, open: true, NineWithSevenOffline());
        Answer(SendOutcome.Sent, 1, 2, 4, 5, 6, 8, 9);
        Answer(SendOutcome.NotEnoughSpace, 3);
        _sender.Sends.Clear();

        _send.StripAction();

        Assert.Equal([3, 7], _sender.Sends.Select(s => int.Parse(s.Pc.DeviceId[^2..])).Order());
        Assert.All(_sender.Sends, s => Assert.Same(Quiz, s.Package));
        Assert.All(_sender.Sends, s => Assert.True(s.Open));
        Assert.True(_send.IsRunning);
        Assert.Equal(TileSendMark.Sending, Tile(7).SendMark);
        Assert.Equal(TileSendMark.None, Tile(1).SendMark);
        Assert.Equal("0 of 2 have it.", _send.StripText);
    }

    [Fact]
    public void Stop_MarksOnlyThePcsStillReceiving()
    {
        _send.Start(Quiz, open: false, NineWithSevenOffline());
        Answer(SendOutcome.Sent, 1, 2);

        _send.StripAction();

        Assert.False(_send.IsRunning);
        Assert.Equal(TileSendMark.Done, Tile(1).SendMark);
        Assert.Equal(TileSendMark.Done, Tile(2).SendMark);
        Assert.Equal("Not sent: offline.", Tile(7).SendDetail);
        Assert.All([3, 4, 5, 6, 8, 9], n => Assert.Equal(TileSendMark.NotSent, Tile(n).SendMark));
        Assert.All([3, 4, 5, 6, 8, 9], n => Assert.Equal("Not sent: stopped.", Tile(n).SendDetail));
        Assert.Equal(SendStripKind.SomeMissed, _send.StripKind);
        Assert.Equal("7 PCs did not get it.", _send.StripText);
    }

    [Theory]
    [InlineData(null, "Not sent: offline.")]
    [InlineData(SendOutcome.NotEnoughSpace, "Not sent: not enough space.")]
    [InlineData(SendOutcome.InUse, "Not sent: the old copy is in use.")]
    [InlineData(SendOutcome.BadPath, "Not sent.")]
    [InlineData(SendOutcome.Busy, "Not sent.")]
    [InlineData(SendOutcome.Failed, "Not sent.")]
    public void EachOutcome_MapsToItsReason(SendOutcome? outcome, string detail)
    {
        _send.Start(Quiz, open: false, EightOnline());

        Answer(outcome, 1);

        Assert.Equal(TileSendMark.NotSent, Tile(1).SendMark);
        Assert.Equal(detail, Tile(1).SendDetail);
    }

    [Fact]
    public void AConnectionThatEnds_IsConnectionLost_EvenWhenItEndsAsCancelled()
    {
        _send.Start(Quiz, open: false, EightOnline());

        SendTo(1).Answer.SetException(new IOException("the stream was aborted"));
        SendTo(2).Answer.SetCanceled();

        Assert.Equal("Not sent: the connection was lost.", Tile(1).SendDetail);
        Assert.Equal("Not sent: the connection was lost.", Tile(2).SendDetail);
        Assert.True(_send.IsRunning);
    }

    [Fact]
    public void ASourceFileThatCannotBeRead_StopsEveryPc_AndNamesTheFile()
    {
        _send.Start(Quiz, open: false, NineWithSevenOffline());
        Answer(SendOutcome.Sent, 1);

        SendTo(2).Answer.SetException(new SendSourceException("a.mp4", new IOException("locked")));

        Assert.False(_send.IsRunning);
        Assert.Equal(TileSendMark.Done, Tile(1).SendMark);
        Assert.All([2, 3, 4, 5, 6, 8, 9], n => Assert.Equal("Not sent: stopped.", Tile(n).SendDetail));
        Assert.Equal(SendStripKind.SourceFailed, _send.StripKind);
        Assert.Equal("Sending stopped", _send.StripTitle);
        Assert.Equal("a.mp4 could not be read.", _send.StripText);
        Assert.False(_send.HasStripAction);
        Assert.True(_send.IsStripClosable);
    }

    [Fact]
    public void WhileRunning_StartIsIgnored()
    {
        _send.Start(Quiz, open: false, NineWithSevenOffline());

        _send.Start(FakeFileSender.Package(("other.txt", 5)), open: true, [Pc(1)]);

        Assert.Equal(8, _sender.Sends.Count);
        Assert.Equal("Sending quiz-chapter-3.html", _send.StripTitle);
        Assert.Equal("0 of 9 have it.", _send.StripText);
    }
}
