namespace Promin.Agent;

/// <summary>The full-screen lock window of an agent PC.</summary>
internal interface ILockScreen : IDisposable
{
    /// <summary>Covers every screen with <paramref name="message"/> (empty: the default text). False when no window could be made.</summary>
    bool Show(string message);

    void Hide();

    /// <summary>True while a broadcast picture must stay above the lock window; false puts the lock window back on top.</summary>
    void Yield(bool toPicture);
}

/// <summary>The block on the keyboard and mouse of an agent PC.</summary>
internal interface IInputBlock : IDisposable
{
    void Hold();

    void Release();
}

/// <summary>
/// Whether an agent PC is locked, and the order things happen in: the lock is written to its file before the
/// window shows and removed from it after the window goes, so a power cut leaves the PC locked or not, never
/// half. Input is blocked while the PC is locked or a broadcast picture is up, and stays blocked until both end.
/// The log gets a line for each lock, unlock and failure, never the message.
/// </summary>
internal sealed class ScreenLock(LockFile file, ILockScreen screen, IInputBlock input, Action<string> log) : IDisposable
{
    private readonly object gate = new();
    private bool locked;
    private bool pictureUp;
    private bool held;

    public bool IsLocked
    {
        get { lock (gate) { return locked; } }
    }

    /// <summary>Applies the lock the file holds, if any. The file is left as it is, whatever happens.</summary>
    public void Restore()
    {
        lock (gate)
        {
            if (file.Read() is { } message)
            {
                Apply(message, "restored");
            }
        }
    }

    /// <summary>
    /// Locks the PC. Does nothing when it is already locked (the first message stays). When the file cannot be
    /// written there is no lock; when no window can be made the file stays so the next start tries again, and
    /// input is not blocked.
    /// </summary>
    public void Lock(string message)
    {
        lock (gate)
        {
            if (locked)
            {
                return;
            }

            try
            {
                file.Write(message);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                log($"The lock could not be written, so the PC is not locked: {e.Message}");
                return;
            }

            Apply(message, "applied");
        }
    }

    /// <summary>
    /// Takes the lock window down, then removes the file, which is removed even when the PC is not showing a lock.
    /// A file that cannot be removed is logged.
    /// </summary>
    public void Unlock()
    {
        lock (gate)
        {
            // The file can outlive the lock: the window may never have been made, or an earlier removal failed.
            if (locked)
            {
                screen.Hide();
                locked = false;
                Sync();
            }

            try
            {
                file.Clear();
                log("The PC is unlocked.");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                log($"The lock file could not be removed, so the PC locks again at the next start: {e.Message}");
            }
        }
    }

    /// <summary>A broadcast picture is on screen: blocks input, and lets the picture sit above a lock window.</summary>
    public void PictureShown()
    {
        lock (gate)
        {
            pictureUp = true;
            if (locked)
            {
                screen.Yield(toPicture: true);
            }

            Sync();
        }
    }

    public void PictureGone()
    {
        lock (gate)
        {
            pictureUp = false;
            if (locked)
            {
                screen.Yield(toPicture: false);
            }

            Sync();
        }
    }

    /// <summary>Takes the window down and releases input, leaving the file, so the PC locks again at the next start.</summary>
    public void Dispose()
    {
        lock (gate)
        {
            if (locked)
            {
                screen.Hide();
                locked = false;
            }

            pictureUp = false;
            Sync();
        }
    }

    private void Apply(string message, string how)
    {
        if (!screen.Show(message))
        {
            log("The lock window could not be made, so the PC is not locked.");
            return;
        }

        locked = true;
        Sync();
        log($"The PC is locked ({how}).");
    }

    private void Sync()
    {
        var want = locked || pictureUp;
        if (want && !held)
        {
            input.Hold();
        }
        else if (!want && held)
        {
            input.Release();
        }

        held = want;
    }
}
