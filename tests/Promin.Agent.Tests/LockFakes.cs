namespace Promin.Agent.Tests;

/// <summary>A lock window that records what it is asked to do in the list it shares with <see cref="FakeInput"/>.</summary>
internal sealed class FakeScreen(List<string> calls) : ILockScreen
{
    /// <summary>What <see cref="Show"/> returns.</summary>
    public bool CanShow { get; set; } = true;

    /// <summary>Runs when <see cref="Show"/> is called, before it answers.</summary>
    public Action? OnShow { get; set; }

    /// <summary>Runs when <see cref="Hide"/> is called.</summary>
    public Action? OnHide { get; set; }

    public bool Disposed { get; private set; }

    public bool Show(string message)
    {
        calls.Add($"show:{message}");
        OnShow?.Invoke();
        return CanShow;
    }

    public void Hide()
    {
        calls.Add("hide");
        OnHide?.Invoke();
    }

    public void Yield(bool toPicture) => calls.Add($"yield:{toPicture}");

    public void Dispose() => Disposed = true;
}

/// <summary>An input block that records its calls in the list it shares with <see cref="FakeScreen"/>.</summary>
internal sealed class FakeInput(List<string> calls) : IInputBlock
{
    public bool Held { get; private set; }

    public bool Disposed { get; private set; }

    public void Hold()
    {
        calls.Add("hold");
        Held = true;
    }

    public void Release()
    {
        calls.Add("release");
        Held = false;
    }

    public void Dispose() => Disposed = true;
}
