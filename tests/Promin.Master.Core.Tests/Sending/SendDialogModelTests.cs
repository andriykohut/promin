using System.Globalization;
using Promin.Master.Core.Rooms;
using Promin.Master.Core.Sending;

namespace Promin.Master.Core.Tests.Sending;

public sealed class SendDialogModelTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "promin-tests", Guid.NewGuid().ToString("N"));

    public SendDialogModelTests()
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        Directory.CreateDirectory(_directory);
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private string File(string relative, long length)
    {
        var path = Path.Combine(_directory, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = System.IO.File.Create(path);
        stream.SetLength(length);
        return path;
    }

    private static SendTarget Pc(int n, bool online = true) =>
        new(new RoomMember($"device-{n:00}", $"PC-{n:00}", $"192.168.1.{20 + n}", 17843, new DateOnly(2026, 9, 22), true), $"PC-{n:00}", online);

    private static SendTargets Targets(IEnumerable<SendTarget> selected, params SendTarget[] all) => new([.. selected], all);

    /// <summary>PC-01 to PC-10 in the room, PC-07 offline; every online one ticked.</summary>
    private static SendTargets TenWithSevenOffline()
    {
        var all = Enumerable.Range(1, 10).Select(n => Pc(n, online: n != 7)).ToArray();
        return Targets(all.Where(t => t.Online), all);
    }

    [Fact]
    public void StartsEmpty_WithSendUnavailable_AndOpenUnticked()
    {
        var model = new SendDialogModel(TenWithSevenOffline());

        Assert.Empty(model.Items);
        Assert.True(model.IsEmpty);
        Assert.Equal(string.Empty, model.SummaryText);
        Assert.False(model.CanSend);
        Assert.False(model.OpenOnArrival);
        Assert.False(model.ToAll);
        Assert.Empty(model.Picked);
    }

    [Fact]
    public void AddFiles_ListsEachWithItsSize_AndSkipsANameAlreadyThere()
    {
        var model = new SendDialogModel(TenWithSevenOffline());
        var quiz = File("quiz-chapter-3.html", 49_152);
        var video = File("lesson-video.mp4", 327_155_712);
        var otherQuiz = File(@"elsewhere\Quiz-Chapter-3.html", 10);

        model.AddFiles([quiz, video, otherQuiz]);
        model.AddFiles([video]);

        Assert.Equal(["quiz-chapter-3.html", "lesson-video.mp4"], model.Items.Select(i => i.Name));
        Assert.Equal(["48 KB", "312 MB"], model.Items.Select(i => i.SizeText));
        Assert.All(model.Items, i => Assert.False(i.IsFolder));
        Assert.Equal([new PickedItem(quiz, false), new PickedItem(video, false)], model.Picked);
        Assert.False(model.IsEmpty);
    }

    [Fact]
    public void AddFolder_ShowsTheTotalOfEverythingInIt()
    {
        var model = new SendDialogModel(TenWithSevenOffline());
        File(@"quiz-chapter-3_files\a.png", 1_000_000);
        File(@"quiz-chapter-3_files\sub\b.png", 258_291);
        var folder = Path.Combine(_directory, "quiz-chapter-3_files");

        model.AddFolder(folder);

        var item = Assert.Single(model.Items);
        Assert.Equal("quiz-chapter-3_files", item.Name);
        Assert.Equal("1.2 MB", item.SizeText);
        Assert.True(item.IsFolder);
        Assert.Equal(new PickedItem(folder, true), item.Picked);
    }

    [Fact]
    public void AFolderWithATrailingSeparator_IsNamedAfterTheFolder()
    {
        var model = new SendDialogModel(TenWithSevenOffline());
        File(@"Lesson\a.txt", 5);

        model.AddFolder(Path.Combine(_directory, "Lesson") + Path.DirectorySeparatorChar);

        Assert.Equal("Lesson", Assert.Single(model.Items).Name);
    }

    [Fact]
    public void ADriveRoot_IsNotAdded()
    {
        var model = new SendDialogModel(TenWithSevenOffline());

        model.AddFolder(Path.GetPathRoot(_directory)!);

        Assert.Empty(model.Items);
    }

    [Fact]
    public void AnItemThatCannotBeRead_IsNotAdded()
    {
        var model = new SendDialogModel(TenWithSevenOffline());

        model.AddFolder(Path.Combine(_directory, "gone"));
        model.AddFiles([Path.Combine(_directory, "gone.txt")]);

        Assert.Empty(model.Items);
    }

    [Fact]
    public void Remove_TakesItOut()
    {
        var model = new SendDialogModel(TenWithSevenOffline());
        model.AddFiles([File("a.txt", 1), File("b.txt", 1)]);

        model.Remove(model.Items[0]);

        Assert.Equal("b.txt", Assert.Single(model.Items).Name);
        Assert.Equal("1 item · 1 KB", model.SummaryText);
        Assert.Equal("b.txt", Path.GetFileName(Assert.Single(model.Picked).Path));
    }

    [Fact]
    public void Remove_OfTheLastItem_LeavesTheListEmptyAndSendUnavailable()
    {
        var model = new SendDialogModel(TenWithSevenOffline());
        model.AddFiles([File("a.txt", 1)]);

        model.Remove(model.Items.Single());

        Assert.True(model.IsEmpty);
        Assert.Equal(string.Empty, model.SummaryText);
        Assert.False(model.CanSend);
    }

    [Fact]
    public void NothingSelected_StartsOnAllPcs_AndTheSelectedChoiceIsUnavailable()
    {
        var all = Enumerable.Range(1, 3).Select(n => Pc(n)).ToArray();

        var model = new SendDialogModel(Targets([], all));

        Assert.True(model.ToAll);
        Assert.False(model.CanChooseSelected);
        Assert.Equal("All 3 PCs", model.AllChoiceText);
        Assert.Equal(all, model.Chosen);
    }

    [Fact]
    public void SomeSelected_StartOnThem_AndTheChoicesNameTheCounts()
    {
        var model = new SendDialogModel(TenWithSevenOffline());

        Assert.False(model.ToAll);
        Assert.True(model.CanChooseSelected);
        Assert.Equal("The 9 selected PCs", model.SelectedChoiceText);
        Assert.Equal("All 10 PCs", model.AllChoiceText);
        Assert.Equal(9, model.Chosen.Count);

        model.ToAll = true;

        Assert.Equal(10, model.Chosen.Count);
    }

    [Fact]
    public void OneSelectedAndOnePc_ReadAsSingular()
    {
        var only = Pc(1);

        var model = new SendDialogModel(Targets([only], only));

        Assert.Equal("The selected PC", model.SelectedChoiceText);
        Assert.Equal("The only PC", model.AllChoiceText);
    }

    [Fact]
    public void OfflineText_NamesTheOneOfflinePc_AmongTheChosen()
    {
        var model = new SendDialogModel(TenWithSevenOffline());

        Assert.Equal(string.Empty, model.OfflineText);

        model.ToAll = true;

        Assert.Equal("PC-07 is offline and will not get them. You can send again later.", model.OfflineText);
    }

    [Fact]
    public void OfflineText_CountsSeveral()
    {
        var all = Enumerable.Range(1, 5).Select(n => Pc(n, online: n > 3)).ToArray();

        var model = new SendDialogModel(Targets([], all));

        Assert.Equal("3 PCs are offline and will not get them. You can send again later.", model.OfflineText);
    }

    [Fact]
    public void CanSend_NeedsAnItemAndAnOnlinePc()
    {
        var everyPcOffline = new SendDialogModel(Targets([], Pc(1, online: false), Pc(2, online: false)));
        everyPcOffline.AddFiles([File("a.txt", 1)]);

        Assert.False(everyPcOffline.CanSend);

        var oneOnline = new SendDialogModel(Targets([], Pc(1, online: false), Pc(2)));

        Assert.False(oneOnline.CanSend);

        oneOnline.AddFiles([File("b.txt", 1)]);

        Assert.True(oneOnline.CanSend);
    }

    [Fact]
    public void SummaryText_CountsItemsAndTotal()
    {
        var model = new SendDialogModel(TenWithSevenOffline());
        File(@"quiz-chapter-3_files\a.png", 1_258_291);

        model.AddFiles([File("quiz-chapter-3.html", 49_152)]);
        model.AddFolder(Path.Combine(_directory, "quiz-chapter-3_files"));
        model.AddFiles([File("lesson-video.mp4", 327_155_712)]);

        Assert.Equal("3 items · 313 MB", model.SummaryText);
    }

    [Fact]
    public void WhenTheListChanges_TheSummaryAlreadyAgreesWithIt()
    {
        var model = new SendDialogModel(TenWithSevenOffline());
        var seen = new List<string>();
        model.Items.CollectionChanged += (_, _) => seen.Add(model.SummaryText);

        model.AddFiles([File("a.txt", 1_024), File("b.txt", 3_072)]);
        model.Remove(model.Items[0]);

        Assert.Equal(["1 item · 1 KB", "2 items · 4 KB", "1 item · 3 KB"], seen);
    }

    [Fact]
    public void ChangesRaiseTheTextsTheyAffect()
    {
        var model = new SendDialogModel(TenWithSevenOffline());
        var raised = new List<string?>();
        model.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        model.AddFiles([File("a.txt", 1)]);
        Assert.Contains(nameof(SendDialogModel.SummaryText), raised);
        Assert.Contains(nameof(SendDialogModel.IsEmpty), raised);
        Assert.Contains(nameof(SendDialogModel.CanSend), raised);
        Assert.Contains(nameof(SendDialogModel.Picked), raised);

        raised.Clear();
        model.ToAll = true;
        Assert.Contains(nameof(SendDialogModel.ToAll), raised);
        Assert.Contains(nameof(SendDialogModel.Chosen), raised);
        Assert.Contains(nameof(SendDialogModel.OfflineText), raised);
        Assert.Contains(nameof(SendDialogModel.CanSend), raised);
    }
}
