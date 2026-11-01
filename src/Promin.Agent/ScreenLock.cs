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
    /// <summary>Blocks keyboard and mouse. False when it could not, with the reason logged: neither is blocked then.</summary>
    bool Hold();

    void Release();
}

/// <summary>
/// Whether an agent PC is locked, and the order things happen in: the lock is written to its file before the
/// window shows and removed from it after the window goes, so a power cut leaves the PC locked or not, never
/// half. Input is blocked while the PC is locked or a broadcast picture is up, and stays blocked until both end.
/// The log gets a line for each lock, unlock and failure, never the message; a restore that keeps failing gets its
/// lines once.
/// </summary>
internal sealed class ScreenLock(LockFile file, ILockScreen screen, IInputBlock input, LockLog log) : IDisposable
{
    private readonly Lock _gate = new();
    private bool _locked;
    private bool _pictureUp;
    private bool _held;

    /// <summary>
    /// <see cref="Restore"/> has locked a PC that was not locked. Raised on the thread that called it, with the gate
    /// free. A handler must not throw. <see cref="Lock"/> does not raise it: its caller knows when to look.
    /// </summary>
    public event Action? Restored;

    public bool IsLocked
    {
        get { lock (_gate) { return _locked; } }
    }

    /// <summary>
    /// Applies the lock the file holds, if any, and raises <see cref="Restored"/> when that locks the PC. Does nothing
    /// when the PC is already locked. The file is left as it is, whatever happens. The agent calls this at start and
    /// every few seconds after that, so a lock that could not be applied takes effect by itself once it can: a
    /// <see cref="Lock"/> whose window failed locks the PC when the window can be made, and an <see cref="Unlock"/> whose
    /// file could not be removed locks it again, so the console reads Locked, which is the truth. A
    /// <see cref="Lock"/> or <see cref="Unlock"/> that arrives meanwhile waits for it, at most the five seconds the window
    /// is given.
    /// </summary>
    public void Restore()
    {
        bool restored;
        lock (_gate)
        {
            if (_locked)
            {
                return;
            }

            log.Quietly(() =>
            {
                if (file.Read() is { } message)
                {
                    Apply(message, "restored");
                }
            });
            restored = _locked;
        }

        if (restored)
        {
            Restored?.Invoke();
        }
    }

    /// <summary>
    /// Locks the PC. Does nothing when it is already locked (the first message stays). When the file cannot be
    /// written there is no lock. When no window can be made, or input cannot be blocked, the PC is not locked and the
    /// window is not left up, and the file stays, so <see cref="Restore"/> tries again and the lock takes effect by
    /// itself once it can.
    /// </summary>
    public void Lock(string message)
    {
        lock (_gate)
        {
            if (_locked)
            {
                return;
            }

            try
            {
                file.Write(message);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                log.Write($"The lock could not be written, so the PC is not locked: {e.Message}");
                return;
            }

            Apply(message, "applied");
        }
    }

    /// <summary>
    /// Takes the lock window down, then removes the file, which is removed even when the PC is not showing a lock.
    /// A file that cannot be removed is logged, and <see cref="Restore"/> then locks the PC again.
    /// </summary>
    public void Unlock()
    {
        lock (_gate)
        {
            // The file can outlive the lock: the window may never have been made, or an earlier removal failed.
            if (_locked)
            {
                screen.Hide();
                _locked = false;
                Sync();
            }

            try
            {
                file.Clear();
                log.Write("The PC is unlocked.");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                log.Write($"The lock file could not be removed, so the PC locks again within seconds: {e.Message}");
            }
        }
    }

    /// <summary>
    /// A broadcast picture is on screen: blocks input, and lets the picture sit above a lock window. Input that cannot
    /// be blocked is logged, and the picture stays.
    /// </summary>
    public void PictureShown()
    {
        lock (_gate)
        {
            _pictureUp = true;
            if (_locked)
            {
                screen.Yield(toPicture: true);
            }

            Sync();
            if (!_held)
            {
                log.Write("Keyboard and mouse could not be blocked, so the picture shows with them free.");
            }
        }
    }

    public void PictureGone()
    {
        lock (_gate)
        {
            _pictureUp = false;
            if (_locked)
            {
                screen.Yield(toPicture: false);
            }

            Sync();
        }
    }

    /// <summary>
    /// Takes the window down and releases input, leaving the file, so the PC locks again at the next start, then
    /// disposes the screen and the input, which nothing else holds.
    /// </summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_locked)
            {
                screen.Hide();
                _locked = false;
            }

            _pictureUp = false;
            Sync();
            screen.Dispose();
            input.Dispose();
        }
    }

    private void Apply(string message, string how)
    {
        if (!screen.Show(message))
        {
            log.Write("The lock window could not be made, so the PC is not locked.");
            return;
        }

        _locked = true;
        Sync();
        if (!_held)
        {
            screen.Hide();
            _locked = false;
            log.Write("Keyboard and mouse could not be blocked, so the PC is not locked.");
            return;
        }

        log.Forget();
        log.Write($"The PC is locked ({how}).");
    }

    private void Sync()
    {
        var want = _locked || _pictureUp;
        if (want && !_held)
        {
            _held = input.Hold();
        }
        else if (!want && _held)
        {
            input.Release();
            _held = false;
        }
    }
}
