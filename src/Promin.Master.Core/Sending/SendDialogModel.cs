using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace Promin.Master.Core.Sending;

/// <summary>One row of the send window's list: <paramref name="SizeText"/> is a file's size, or the total of everything in a folder.</summary>
public sealed record SendListItem(string Name, string SizeText, bool IsFolder, PickedItem Picked);

/// <summary>
/// What the send window says and decides: the files and folders to send, which PCs they go to, and whether Send is
/// available. The window binds to it and calls into it; nothing on the page decides anything.
/// </summary>
/// <remarks>
/// The list is kept to what <see cref="SendPackage"/> can carry: a name is listed once (names compare without regard
/// to case, as on the PC that receives them), and a drive root, which has no name, is not listed.
/// </remarks>
public sealed class SendDialogModel(SendTargets targets) : INotifyPropertyChanged
{
    private readonly Dictionary<SendListItem, long> _bytes = [];
    private bool _toAll = targets.Selected.Count == 0;

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<SendListItem> Items { get; } = [];

    /// <summary>
    /// Whether the send goes to every PC in the room, not only the selected ones. Starts true when no PC is selected.
    /// </summary>
    public bool ToAll
    {
        get => _toAll;
        set
        {
            if (_toAll == value)
            {
                return;
            }

            _toAll = value;
            Raise();
            Raise(nameof(Chosen));
            Raise(nameof(OfflineText));
            Raise(nameof(CanSend));
        }
    }

    public bool CanChooseSelected => targets.Selected.Count > 0;

    public bool OpenOnArrival { get; set; }

    public string SelectedChoiceText => Phrase.Format(Strings.Send_Selected, ("count", targets.Selected.Count));

    public string AllChoiceText => Phrase.Format(Strings.Send_All, ("count", targets.All.Count));

    /// <summary>Names the chosen PCs that are offline and will not get the send; empty when they are all online.</summary>
    public string OfflineText => Chosen.Where(t => !t.Online).ToList() switch
    {
        [] => string.Empty,
        [var one] => Phrase.Format(Strings.Send_OfflineOne, ("name", one.Name)),
        var several => Phrase.Format(Strings.Send_OfflineMany, ("count", several.Count)),
    };

    /// <summary>The number of items and their total size; empty while the list is empty.</summary>
    public string SummaryText => IsEmpty
        ? string.Empty
        : Phrase.Format(
            Strings.Send_Summary,
            ("count", Items.Count),
            ("size", SendText.Size(_bytes.Values.Sum(), CultureInfo.CurrentCulture)));

    public bool IsEmpty => Items.Count == 0;

    /// <summary>The list has an item, and at least one chosen PC is online.</summary>
    public bool CanSend => !IsEmpty && Chosen.Any(t => t.Online);

    public IReadOnlyList<SendTarget> Chosen => _toAll ? targets.All : targets.Selected;

    public IReadOnlyList<PickedItem> Picked => Items.Select(i => i.Picked).ToList();

    /// <summary>Lists each file, skipping one whose name is already listed or that cannot be read.</summary>
    public void AddFiles(IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        foreach (var path in paths)
        {
            Add(new PickedItem(path, IsFolder: false));
        }
    }

    /// <summary>Lists the folder with the total of everything in it, unless its name is already listed or it cannot be read.</summary>
    public void AddFolder(string path) => Add(new PickedItem(path, IsFolder: true));

    public void Remove(SendListItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (Items.Remove(item))
        {
            _bytes.Remove(item);
            ListChanged();
        }
    }

    private void Add(PickedItem picked)
    {
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(picked.Path));
        if (name.Length == 0 || Items.Any(i => string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        long bytes;
        try
        {
            bytes = SendPackage.SizeOf(picked);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // What cannot be read now cannot be sent later either.
            return;
        }

        var item = new SendListItem(name, SendText.Size(bytes, CultureInfo.CurrentCulture), picked.IsFolder, picked);
        Items.Add(item);
        _bytes[item] = bytes;
        ListChanged();
    }

    private void ListChanged()
    {
        Raise(nameof(SummaryText));
        Raise(nameof(IsEmpty));
        Raise(nameof(CanSend));
        Raise(nameof(Picked));
    }

    private void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
