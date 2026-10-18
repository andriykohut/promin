using Promin.Protocol.Messages;
using Promin.Protocol.Sending;

namespace Promin.Protocol.Tests.TestDoubles;

/// <summary>
/// Keeps what a send delivers in memory, and refuses where it is told to.
/// </summary>
public sealed class MemoryReceiveTarget : IReceiveTarget
{
    private readonly TaskCompletionSource _began = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _itemCount;

    /// <summary>When set, <see cref="Begin"/> refuses with this outcome.</summary>
    public SendOutcome? RefuseAtBegin { get; init; }

    /// <summary>When set, the Nth item (from 1, folders and files alike) is refused with this outcome.</summary>
    public (int Item, SendOutcome Outcome)? RefuseAtItem { get; init; }

    public SendOutcome FinishWith { get; init; } = SendOutcome.Sent;

    public long? Total { get; private set; }

    /// <summary>Completes once <see cref="Begin"/> has accepted the send.</summary>
    public Task Began => _began.Task;

    /// <summary>Folders and files, in the order they arrived.</summary>
    public List<(string Path, bool IsFolder)> Items { get; } = [];

    public Dictionary<string, MemoryStream> Files { get; } = [];

    public bool Finished { get; private set; }

    public bool Discarded { get; private set; }

    public void Begin(long totalBytes)
    {
        if (RefuseAtBegin is { } outcome)
        {
            throw new SendRefusedException(outcome);
        }

        Total = totalBytes;
        _began.SetResult();
    }

    public void AddFolder(string path) => Add(path, isFolder: true);

    public Stream AddFile(string path, long length)
    {
        Add(path, isFolder: false);
        return Files[path] = new MemoryStream();
    }

    public SendOutcome Finish()
    {
        Finished = true;
        return FinishWith;
    }

    public void Discard() => Discarded = true;

    private void Add(string path, bool isFolder)
    {
        if (RefuseAtItem is { } refusal && ++_itemCount == refusal.Item)
        {
            throw new SendRefusedException(refusal.Outcome);
        }

        Items.Add((path, isFolder));
    }
}
