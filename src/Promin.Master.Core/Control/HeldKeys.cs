using Promin.Protocol.Messages;

namespace Promin.Master.Core.Control;

/// <summary>The keys sent down to the agent PC and not yet up, so that none stays pressed there when control stops or the view loses focus.</summary>
public sealed class HeldKeys
{
    private readonly List<KeyInput> _down = [];

    public void Down(KeyInput key)
    {
        Remove(key);
        _down.Add(key with { Down = true });
    }

    public void Up(KeyInput key) => Remove(key);

    /// <summary>An up for each key still down, in the order they went down. Nothing is held afterwards.</summary>
    public IReadOnlyList<KeyInput> ReleaseAll()
    {
        var ups = _down.Select(key => key with { Down = false }).ToArray();
        _down.Clear();
        return ups;
    }

    private void Remove(KeyInput key) =>
        _down.RemoveAll(held => held.VirtualKey == key.VirtualKey && held.Extended == key.Extended);
}
