using System.Security.Cryptography.X509Certificates;
using Promin.Protocol.Transport;

namespace Promin.Protocol.Tests.Transport;

public class PeerCertificateStoreTests
{
    [Fact]
    public void Pin_ThenTryGetPinnedThumbprint_ReturnsTheSameValue()
    {
        var store = new PeerCertificateStore();

        store.Pin("agent-1", "ABC123");
        var found = store.TryGetPinnedThumbprint("agent-1", out var thumbprint);

        Assert.True(found);
        Assert.Equal("ABC123", thumbprint);
    }

    [Fact]
    public void TryGetPinnedThumbprint_ReturnsFalse_ForUnknownDevice()
    {
        var store = new PeerCertificateStore();

        var found = store.TryGetPinnedThumbprint("unknown-device", out var thumbprint);

        Assert.False(found);
        Assert.Null(thumbprint);
    }

    [Fact]
    public void SaveThenLoad_RoundTripsPinnedThumbprints()
    {
        var path = Path.GetTempFileName();
        try
        {
            var store = new PeerCertificateStore();
            store.Pin("agent-1", "ABC123");
            store.Save(path);

            var reloaded = new PeerCertificateStore();
            reloaded.Load(path);

            Assert.True(reloaded.TryGetPinnedThumbprint("agent-1", out var thumbprint));
            Assert.Equal("ABC123", thumbprint);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// Fails an implementation that keys every peer under one literal string
    /// (the brief pinned the master under "master"), which would make any
    /// certificate match any device and allow only one paired master.
    /// </summary>
    [Fact]
    public void Pin_KeepsDistinctThumbprints_PerDeviceId()
    {
        var store = new PeerCertificateStore();

        store.Pin("master-a", "AAAA");
        store.Pin("master-b", "BBBB");

        Assert.True(store.TryGetPinnedThumbprint("master-a", out var a));
        Assert.True(store.TryGetPinnedThumbprint("master-b", out var b));
        Assert.Equal("AAAA", a);
        Assert.Equal("BBBB", b);
    }

    /// <summary>
    /// A pin is never silently replaced: that is how a pairing window would re-key a machine
    /// that is already trusted. Fails the implementation that overwrote the earlier pin.
    /// </summary>
    [Fact]
    public void Pin_RefusesToReplaceADifferentThumbprint_ForTheSameDevice()
    {
        var store = new PeerCertificateStore();
        store.Pin("agent-1", "OLD");

        var refusal = Assert.Throws<PinConflictException>(() => store.Pin("agent-1", "NEW"));

        Assert.Contains("already pinned", refusal.Message);
        Assert.True(store.TryGetPinnedThumbprint("agent-1", out var thumbprint));
        Assert.Equal("OLD", thumbprint);
        Assert.False(store.TryGetDeviceIdForThumbprint("NEW", out _));
    }

    /// <summary>Pinning what is already pinned is not a conflict — a retried pairing must work.</summary>
    [Fact]
    public void Pin_IsIdempotent_ForTheSameThumbprint()
    {
        var store = new PeerCertificateStore();
        store.Pin("agent-1", "AABBCC");

        store.Pin("agent-1", "aabbcc");

        Assert.True(store.TryGetPinnedThumbprint("agent-1", out var thumbprint));
        Assert.Equal("AABBCC", thumbprint);
    }

    /// <summary>One certificate identifies one device; it cannot be claimed under a second id.</summary>
    [Fact]
    public void Pin_RefusesAThumbprintAlreadyPinnedForAnotherDevice()
    {
        var store = new PeerCertificateStore();
        store.Pin("agent-1", "AABBCC");

        Assert.Throws<PinConflictException>(() => store.Pin("agent-2", "aabbcc"));

        Assert.False(store.TryGetPinnedThumbprint("agent-2", out _));
        Assert.True(store.TryGetDeviceIdForThumbprint("AABBCC", out var owner));
        Assert.Equal("agent-1", owner);
    }

    /// <summary>Re-pairing a reimaged machine is an explicit unpin, then a fresh pin.</summary>
    [Fact]
    public void Unpin_ThenPin_AcceptsANewThumbprint()
    {
        var store = new PeerCertificateStore();
        store.Pin("agent-1", "OLD");

        Assert.True(store.Unpin("agent-1"));
        Assert.False(store.TryGetPinnedThumbprint("agent-1", out _));

        store.Pin("agent-1", "NEW");

        Assert.True(store.TryGetPinnedThumbprint("agent-1", out var thumbprint));
        Assert.Equal("NEW", thumbprint);
    }

    [Fact]
    public void Unpin_ReturnsFalse_ForADeviceThatWasNeverPinned()
    {
        Assert.False(new PeerCertificateStore().Unpin("never-paired"));
    }

    /// <summary>
    /// The agent side cannot know which master is dialling until TLS presents a
    /// certificate, so it must resolve device id from thumbprint. Fails an
    /// implementation with only a forward map.
    /// </summary>
    [Fact]
    public void TryGetDeviceIdForThumbprint_ResolvesThePinnedDevice_CaseInsensitively()
    {
        var store = new PeerCertificateStore();
        store.Pin("master-a", "AABBCC");

        Assert.True(store.TryGetDeviceIdForThumbprint("aabbcc", out var deviceId));
        Assert.Equal("master-a", deviceId);

        Assert.False(store.TryGetDeviceIdForThumbprint("DDEEFF", out var missing));
        Assert.Null(missing);
    }

    /// <summary>Fails a store whose reverse index outlives the pin it was built from.</summary>
    [Fact]
    public void TryGetDeviceIdForThumbprint_ForgetsAThumbprintThatWasUnpinned()
    {
        var store = new PeerCertificateStore();
        store.Pin("agent-1", "OLD");
        store.Unpin("agent-1");
        store.Pin("agent-1", "NEW");

        Assert.False(store.TryGetDeviceIdForThumbprint("OLD", out _));
        Assert.True(store.TryGetDeviceIdForThumbprint("NEW", out var deviceId));
        Assert.Equal("agent-1", deviceId);
    }

    [Fact]
    public void Load_ReplacesAnyPinsHeldInMemory()
    {
        var path = Path.GetTempFileName();
        try
        {
            var saved = new PeerCertificateStore();
            saved.Pin("agent-1", "ABC123");
            saved.Save(path);

            var store = new PeerCertificateStore();
            store.Pin("stale-agent", "DEAD");
            store.Load(path);

            Assert.False(store.TryGetPinnedThumbprint("stale-agent", out _));
            Assert.False(store.TryGetDeviceIdForThumbprint("DEAD", out _));
            Assert.True(store.TryGetPinnedThumbprint("agent-1", out _));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_OnAMissingFile_LeavesAnEmptyStore()
    {
        var store = new PeerCertificateStore();

        store.Load(Path.Combine(Path.GetTempPath(), $"promin-absent-{Guid.NewGuid():N}.json"));

        Assert.False(store.TryGetPinnedThumbprint("agent-1", out _));
    }

    /// <summary>
    /// The pin file is the device's entire trust state. Saving over an existing file must swap
    /// a complete new file in, and must not leave its temporary file behind. Fails an
    /// implementation that writes a temporary file and forgets it, and one whose replace step
    /// does not work when the destination already exists.
    /// </summary>
    [Fact]
    public void Save_ReplacesAnExistingFileWhole_AndLeavesNoTemporaryFileBehind()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"promin-pins-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "pins.json");
        try
        {
            var store = new PeerCertificateStore();
            store.Pin("agent-1", "ABC123");
            store.Save(path);                 // destination does not exist yet
            Assert.Equal(["pins.json"], Directory.GetFiles(directory).Select(Path.GetFileName));

            store.Pin("agent-2", "DEF456");
            store.Save(path);                 // destination exists: replaced, not appended to
            Assert.Equal(["pins.json"], Directory.GetFiles(directory).Select(Path.GetFileName));

            var reloaded = new PeerCertificateStore();
            reloaded.Load(path);
            Assert.True(reloaded.TryGetPinnedThumbprint("agent-1", out var first));
            Assert.True(reloaded.TryGetPinnedThumbprint("agent-2", out var second));
            Assert.Equal("ABC123", first);
            Assert.Equal("DEF456", second);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// The save is a swap, not an in-place write. A reader holding the old file open (sharing
    /// delete but not write) keeps seeing the complete old contents while the path already
    /// holds the complete new ones — there is no moment at which the file is truncated or
    /// half-written. Fails File.WriteAllText onto the destination, which cannot even open the
    /// file under this reader, and which truncates before it writes whenever it can.
    /// </summary>
    [Fact]
    public void Save_SwapsTheFileAtomically_UnderAReaderHoldingTheOldOne()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"promin-pins-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "pins.json");
        try
        {
            var store = new PeerCertificateStore();
            store.Pin("agent-1", "ABC123");
            store.Save(path);
            var before = File.ReadAllBytes(path);

            store.Pin("agent-2", "DEF456");
            using (var reader = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
            {
                store.Save(path);

                var seen = new byte[before.Length + 64];
                var read = reader.ReadAtLeast(seen, seen.Length, throwOnEndOfStream: false);
                Assert.Equal(before, seen[..read]);
            }

            var reloaded = new PeerCertificateStore();
            reloaded.Load(path);
            Assert.True(reloaded.TryGetPinnedThumbprint("agent-1", out _));
            Assert.True(reloaded.TryGetPinnedThumbprint("agent-2", out _));
            Assert.Equal(["pins.json"], Directory.GetFiles(directory).Select(Path.GetFileName));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// A save whose swap fails must leave the previous pin file exactly as it was and must not
    /// leave its temporary file lying next to it. The destination is held open without sharing,
    /// so the swap cannot happen. Fails an implementation that forgets its temporary file on
    /// the failure path.
    /// </summary>
    [Fact]
    public void Save_ThatFails_LeavesThePreviousFileIntact_AndNoTemporaryFile()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"promin-pins-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "pins.json");
        try
        {
            var original = new PeerCertificateStore();
            original.Pin("agent-1", "ABC123");
            original.Save(path);
            var before = File.ReadAllBytes(path);

            var changed = new PeerCertificateStore();
            changed.Pin("agent-9", "FFFF");
            using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Assert.ThrowsAny<IOException>(() => changed.Save(path));
            }

            Assert.Equal(before, File.ReadAllBytes(path));
            Assert.Equal(["pins.json"], Directory.GetFiles(directory).Select(Path.GetFileName));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ComputeThumbprint_IsStableAndDistinctPerCertificate()
    {
        using var first = SelfSignedCertificateFactory.Create("agent-1");
        using var second = SelfSignedCertificateFactory.Create("agent-1");

        var a = PeerCertificateStore.ComputeThumbprint(first);
        var b = PeerCertificateStore.ComputeThumbprint(X509CertificateLoader.LoadCertificate(first.Export(X509ContentType.Cert)));

        Assert.Equal(a, b);
        Assert.NotEqual(a, PeerCertificateStore.ComputeThumbprint(second));
        // SHA-256, hex: 64 characters. Fails an implementation that fell back to SHA-1.
        Assert.Equal(64, a.Length);
    }
}
