using Promin.Protocol.Messages;

namespace Promin.Agent.Tests;

/// <summary>A control tab that records its calls in the list shared with <see cref="FakeInput"/>.</summary>
internal sealed class FakeTab(List<string> calls) : IControlTab
{
    /// <summary>What <see cref="Show"/> returns.</summary>
    public bool CanShow { get; set; } = true;

    public bool Show()
    {
        calls.Add("tab:show");
        return CanShow;
    }

    public void Hide() => calls.Add("tab:hide");

    public void Dispose()
    {
    }
}

/// <summary>A clipboard watch that records what is written to it and when it is disposed.</summary>
internal sealed class FakeClipboard(List<string> calls) : IClipboardWatch
{
    public event Action<string>? Copied;

    public List<string> Written { get; } = [];

    public void Copy(string text) => Copied?.Invoke(text);

    public void Write(string text) => Written.Add(text);

    /// <summary>Makes <see cref="Dispose"/> throw, after it has recorded itself.</summary>
    public bool ThrowOnDispose { get; set; }

    public void Dispose()
    {
        calls.Add("clipboard:dispose");
        if (ThrowOnDispose)
        {
            throw new InvalidOperationException("clipboard");
        }
    }
}

/// <summary>A control video that runs until cancelled or until <see cref="Fail"/>.</summary>
internal sealed class FakeVideo(List<string> calls) : IControlVideo
{
    private readonly TaskCompletionSource _failure = new();

    public int Keyframes { get; private set; }

    public bool Running { get; private set; }

    public void Fail(Exception failure) => _failure.SetException(failure);

    public void RequestKeyframe() => Keyframes++;

    public async Task RunAsync(CancellationToken ct)
    {
        Running = true;
        try
        {
            await _failure.Task.WaitAsync(ct).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            if (_failure.Task.IsFaulted)
            {
                await _failure.Task.ConfigureAwait(false);
            }
        }
        finally
        {
            Running = false;
        }
    }

    public void Dispose()
    {
        Assert.False(Running, "the video was disposed while it still ran");
        calls.Add("video:dispose");
    }
}

/// <summary>An injector that records every call.</summary>
internal sealed class FakeInjector(List<string> calls) : IInputInjector
{
    /// <summary>Makes <see cref="ReleaseAll"/> throw, after it has recorded itself.</summary>
    public bool ThrowOnRelease { get; set; }

    public void Move(ushort x, ushort y) => calls.Add($"move:{x},{y}");

    public void Button(MouseButton button, bool down) => calls.Add($"button:{button},{down}");

    public void Wheel(int delta, bool horizontal) => calls.Add($"wheel:{delta},{horizontal}");

    public void Key(ushort virtualKey, ushort scanCode, bool extended, bool down) => calls.Add($"key:{virtualKey},{scanCode},{extended},{down}");

    public void ReleaseAll()
    {
        calls.Add("release-all");
        if (ThrowOnRelease)
        {
            throw new InvalidOperationException("injector");
        }
    }
}

/// <summary>A preview switch that records its calls.</summary>
internal sealed class FakePreviews(List<string> calls) : IPreviewSwitch
{
    public Task PauseAsync()
    {
        calls.Add("pause");
        return Task.CompletedTask;
    }

    public void Resume() => calls.Add("resume");
}
