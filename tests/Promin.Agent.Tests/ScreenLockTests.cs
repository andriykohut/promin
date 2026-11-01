namespace Promin.Agent.Tests;

public class ScreenLockTests
{
    private sealed class Rig : IDisposable
    {
        private readonly TempDirectory temp = new();

        public Rig()
        {
            Paths = temp.Paths;
            Screen = new FakeScreen(Calls);
            Input = new FakeInput(Calls);
            LockLog = new LockLog(Log.Add);
            File = new LockFile(Paths.LockFile, LockLog.Write);
            Lock = new ScreenLock(File, Screen, Input, LockLog);
        }

        public AgentPaths Paths { get; }
        public List<string> Calls { get; } = [];
        public List<string> Log { get; } = [];
        public LockLog LockLog { get; }
        public FakeScreen Screen { get; }
        public FakeInput Input { get; }
        public LockFile File { get; }
        public ScreenLock Lock { get; }

        public void Dispose() => temp.Dispose();
    }

    [Fact]
    public void Restore_WithNoFile_ShowsNothing()
    {
        using var rig = new Rig();

        rig.Lock.Restore();

        Assert.False(rig.Lock.IsLocked);
        Assert.Empty(rig.Calls);
    }

    [Fact]
    public void Restore_WithAFile_ShowsItsMessageAndHolds()
    {
        using var rig = new Rig();
        rig.File.Write("Break");

        rig.Lock.Restore();

        Assert.Equal(["show:Break", "hold"], rig.Calls);
        Assert.True(rig.Lock.IsLocked);
    }

    [Fact]
    public void Restore_WithAnEmptyMessage_ShowsTheDefault()
    {
        using var rig = new Rig();
        rig.File.Write("");

        rig.Lock.Restore();

        Assert.Equal("show:", rig.Calls[0]);
        Assert.True(rig.Lock.IsLocked);
    }

    [Fact]
    public void Restore_WithAnUnreadableFile_IsNotLocked_AndSaysSo()
    {
        using var rig = new Rig();
        Directory.CreateDirectory(rig.Paths.AgentDirectory);
        System.IO.File.WriteAllText(rig.Paths.LockFile, "not json");

        rig.Lock.Restore();

        Assert.False(rig.Lock.IsLocked);
        Assert.Empty(rig.Calls);
        Assert.Contains(rig.Log, line => line.Contains(rig.Paths.LockFile));
    }

    [Fact]
    public void Restore_WhenAlreadyLocked_DoesNothing()
    {
        using var rig = new Rig();
        rig.Lock.Lock("Break");
        rig.Calls.Clear();

        rig.Lock.Restore();

        Assert.Empty(rig.Calls);
        Assert.True(rig.Lock.IsLocked);
    }

    [Fact]
    public void Restore_AfterALockWhoseWindowFailed_LocksByItself()
    {
        using var rig = new Rig();
        rig.Screen.CanShow = false;
        rig.Lock.Lock("Break");
        rig.Screen.CanShow = true;

        rig.Lock.Restore();

        Assert.True(rig.Lock.IsLocked);
        Assert.Equal(["show:Break", "show:Break", "hold"], rig.Calls);
    }

    [Fact]
    public void Restore_AfterAnUnlockWhoseFileStayed_LocksAgain()
    {
        using var rig = new Rig();
        rig.Lock.Lock("Break");
        using (new FileStream(rig.Paths.LockFile, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            rig.Lock.Unlock();
        }

        Assert.False(rig.Lock.IsLocked);

        rig.Lock.Restore();

        Assert.True(rig.Lock.IsLocked);
    }

    [Fact]
    public void Restored_IsRaisedOnce_WhenARestoreLocksThePc()
    {
        using var rig = new Rig();
        var raised = 0;
        rig.Lock.Restored += () => raised++;
        rig.File.Write("Break");

        rig.Lock.Restore();
        rig.Lock.Restore();

        Assert.Equal(1, raised);
    }

    [Theory]
    [InlineData("no file")]
    [InlineData("unreadable")]
    [InlineData("window")]
    [InlineData("hold")]
    public void Restored_IsNotRaised_WhenARestoreDoesNotLockThePc(string outcome)
    {
        using var rig = new Rig();
        var raised = 0;
        rig.Lock.Restored += () => raised++;
        Arrange(rig, outcome);

        rig.Lock.Restore();

        Assert.Equal(0, raised);
    }

    [Fact]
    public void Restored_IsNotRaised_ByALockFromTheConsole()
    {
        using var rig = new Rig();
        var raised = 0;
        rig.Lock.Restored += () => raised++;

        rig.Lock.Lock("Break");

        Assert.Equal(0, raised);
    }

    [Fact]
    public void Restored_IsRaisedOutsideTheGate()
    {
        using var rig = new Rig();
        var free = false;
        rig.Lock.Restored += () => free = Task.Run(() => rig.Lock.IsLocked).Wait(TimeSpan.FromSeconds(5));
        rig.File.Write("Break");

        rig.Lock.Restore();

        Assert.True(free);
    }

    [Theory]
    [InlineData("unreadable")]
    [InlineData("window")]
    [InlineData("hold")]
    public void ARestoreThatKeepsFailing_LogsOnce(string outcome)
    {
        using var rig = new Rig();
        Arrange(rig, outcome);

        // What the real window and input block write for themselves goes to the same log.
        rig.Screen.OnShow = () => rig.LockLog.Write("the window's own line");

        rig.Lock.Restore();
        var first = rig.Log.ToList();
        rig.Lock.Restore();
        rig.Lock.Restore();

        Assert.NotEmpty(first);
        Assert.Equal(first, rig.Log);
    }

    [Fact]
    public void AFailureThatChanges_IsLogged()
    {
        using var rig = new Rig();
        Arrange(rig, "window");
        rig.Lock.Restore();
        rig.Screen.CanShow = true;
        rig.Input.CanHold = false;

        rig.Lock.Restore();

        Assert.Contains(rig.Log, line => line.Contains("lock window could not be made"));
        Assert.Contains(rig.Log, line => line.Contains("could not be blocked"));
    }

    [Fact]
    public void AFailureAfterTheLockTook_IsLoggedAgain()
    {
        using var rig = new Rig();
        Arrange(rig, "window");
        rig.Lock.Restore();
        rig.Screen.CanShow = true;
        rig.Lock.Restore();
        rig.Lock.Unlock();
        Arrange(rig, "window");

        rig.Lock.Restore();

        Assert.Equal(2, rig.Log.Count(line => line.Contains("lock window could not be made")));
    }

    [Fact]
    public void ALockFromTheConsole_LogsItsFailure_AndTheNextRestoreDoesNot()
    {
        using var rig = new Rig();
        Arrange(rig, "window");
        rig.Lock.Restore();

        rig.Lock.Lock("Break");
        rig.Lock.Restore();

        Assert.Equal(2, rig.Log.Count(line => line.Contains("lock window could not be made")));
    }

    [Fact]
    public void WhatTheWindowSaysWhileLocked_IsNeverDropped()
    {
        using var rig = new Rig();
        rig.Lock.Lock("Break");

        rig.LockLog.Write("The lock window is at the front.");
        rig.LockLog.Write("The lock window is at the front.");

        Assert.Equal(2, rig.Log.Count(line => line.Contains("at the front")));
    }

    private static void Arrange(Rig rig, string outcome)
    {
        switch (outcome)
        {
            case "unreadable":
                Directory.CreateDirectory(rig.Paths.AgentDirectory);
                System.IO.File.WriteAllText(rig.Paths.LockFile, "not json");
                break;
            case "window":
                rig.File.Write("Break");
                rig.Screen.CanShow = false;
                break;
            case "hold":
                rig.File.Write("Break");
                rig.Input.CanHold = false;
                break;
        }
    }

    [Fact]
    public void Lock_WritesTheFileBeforeTheWindowShows()
    {
        using var rig = new Rig();
        var existedAtShow = false;
        rig.Screen.OnShow = () => existedAtShow = System.IO.File.Exists(rig.Paths.LockFile);

        rig.Lock.Lock("Break");

        Assert.True(existedAtShow);
        Assert.Equal(["show:Break", "hold"], rig.Calls);
    }

    [Fact]
    public void Lock_WhenTheFileCannotBeWritten_StaysUnlocked()
    {
        using var rig = new Rig();
        Directory.CreateDirectory(rig.Paths.LockFile);

        rig.Lock.Lock("Break");

        Assert.False(rig.Lock.IsLocked);
        Assert.Empty(rig.Calls);
        Assert.NotEmpty(rig.Log);
    }

    [Fact]
    public void Lock_WhenTheWindowCannotBeMade_DoesNotHold_AndKeepsTheFile()
    {
        using var rig = new Rig();
        rig.Screen.CanShow = false;

        rig.Lock.Lock("Break");

        Assert.False(rig.Lock.IsLocked);
        Assert.Equal(["show:Break"], rig.Calls);
        Assert.Equal("Break", rig.File.Read());
    }

    [Fact]
    public void Lock_WhenInputCannotBeHeld_TakesTheWindowDown_AndKeepsTheFile()
    {
        using var rig = new Rig();
        rig.Input.CanHold = false;

        rig.Lock.Lock("Break");

        Assert.False(rig.Lock.IsLocked);
        Assert.Equal(["show:Break", "hold", "hide"], rig.Calls);
        Assert.Equal("Break", rig.File.Read());
        Assert.Contains(rig.Log, line => line.Contains("not locked"));
    }

    [Fact]
    public void Restore_WhenInputCannotBeHeld_TakesTheWindowDown_AndKeepsTheFile()
    {
        using var rig = new Rig();
        rig.File.Write("Break");
        rig.Input.CanHold = false;

        rig.Lock.Restore();

        Assert.False(rig.Lock.IsLocked);
        Assert.Equal(["show:Break", "hold", "hide"], rig.Calls);
        Assert.Equal("Break", rig.File.Read());
    }

    [Fact]
    public void PictureShown_WhenInputCannotBeHeld_SaysSo_AndReleasesNothingAfterwards()
    {
        using var rig = new Rig();
        rig.Input.CanHold = false;

        rig.Lock.PictureShown();
        rig.Lock.PictureGone();

        Assert.Equal(["hold"], rig.Calls);
        Assert.Contains(rig.Log, line => line.Contains("could not be blocked"));
    }

    [Fact]
    public void AHoldThatFailed_IsTriedAgain_WhenTheLockComes()
    {
        using var rig = new Rig();
        rig.Input.CanHold = false;
        rig.Lock.PictureShown();
        rig.Input.CanHold = true;

        rig.Lock.Lock("Break");

        Assert.True(rig.Lock.IsLocked);
        Assert.True(rig.Input.Held);
        Assert.Equal(["hold", "show:Break", "hold"], rig.Calls);
    }

    [Fact]
    public void Lock_WhenAlreadyLocked_KeepsTheFirstMessage()
    {
        using var rig = new Rig();
        rig.Lock.Lock("first");

        rig.Lock.Lock("second");

        Assert.Equal("first", rig.File.Read());
        Assert.Equal(["show:first", "hold"], rig.Calls);
    }

    [Fact]
    public void Unlock_RemovesTheLockThenTheFile()
    {
        using var rig = new Rig();
        rig.Lock.Lock("Break");
        var existedAtHide = false;
        rig.Screen.OnHide = () => existedAtHide = System.IO.File.Exists(rig.Paths.LockFile);

        rig.Lock.Unlock();

        Assert.True(existedAtHide);
        Assert.False(System.IO.File.Exists(rig.Paths.LockFile));
        Assert.False(rig.Lock.IsLocked);
        Assert.Equal(["show:Break", "hold", "hide", "release"], rig.Calls);
    }

    [Fact]
    public void Unlock_WhenTheWindowWasNeverMade_StillRemovesTheFile()
    {
        using var rig = new Rig();
        rig.Screen.CanShow = false;
        rig.Lock.Lock("Break");

        rig.Lock.Unlock();

        Assert.False(System.IO.File.Exists(rig.Paths.LockFile));
    }

    [Fact]
    public void TheLog_NeverCarriesTheMessage()
    {
        using var rig = new Rig();

        rig.Lock.Lock("secret words");
        rig.Lock.Unlock();
        rig.Lock.Lock("secret words");
        rig.Lock.Restore();

        Assert.NotEmpty(rig.Log);
        Assert.DoesNotContain(rig.Log, line => line.Contains("secret"));
    }

    [Theory]
    [InlineData("lock-first")]
    [InlineData("picture-first")]
    public void Input_IsHeldUntilBothHaveEnded(string first)
    {
        using var rig = new Rig();
        rig.Lock.Lock("Break");
        rig.Lock.PictureShown();

        if (first == "lock-first")
        {
            rig.Lock.Unlock();
            Assert.True(rig.Input.Held);
            rig.Lock.PictureGone();
        }
        else
        {
            rig.Lock.PictureGone();
            Assert.True(rig.Input.Held);
            rig.Lock.Unlock();
        }

        Assert.False(rig.Input.Held);
    }

    [Fact]
    public void PictureShown_OnAnUnlockedPc_HoldsInput_AndWritesNoFile()
    {
        using var rig = new Rig();

        rig.Lock.PictureShown();

        Assert.True(rig.Input.Held);
        Assert.False(rig.Lock.IsLocked);
        Assert.False(System.IO.File.Exists(rig.Paths.LockFile));
        rig.Lock.PictureGone();
        Assert.False(rig.Input.Held);
    }

    [Fact]
    public void PictureShown_OnALockedPc_YieldsTheScreen_UntilPictureGone()
    {
        using var rig = new Rig();
        rig.Lock.Lock("Break");
        rig.Calls.Clear();

        rig.Lock.PictureShown();
        rig.Lock.PictureGone();

        Assert.Equal(["yield:True", "yield:False"], rig.Calls);
    }

    [Fact]
    public void Dispose_HidesAndReleases_AndLeavesTheFile()
    {
        using var rig = new Rig();
        rig.Lock.Lock("Break");

        rig.Lock.Dispose();

        Assert.False(rig.Input.Held);
        Assert.Equal("Break", rig.File.Read());
        Assert.Equal(["show:Break", "hold", "hide", "release"], rig.Calls);
    }

    [Fact]
    public void Dispose_DisposesTheScreenAndTheInput()
    {
        using var rig = new Rig();

        rig.Lock.Dispose();

        Assert.True(rig.Screen.Disposed);
        Assert.True(rig.Input.Disposed);
    }
}
