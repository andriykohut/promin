using System.Security.Cryptography.X509Certificates;
using Educast.Protocol.Transport;

namespace Educast.Protocol.Tests.Transport;

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

    [Fact]
    public void Pin_ReplacesAnEarlierThumbprint_ForTheSameDevice()
    {
        var store = new PeerCertificateStore();

        store.Pin("agent-1", "OLD");
        store.Pin("agent-1", "NEW");

        Assert.True(store.TryGetPinnedThumbprint("agent-1", out var thumbprint));
        Assert.Equal("NEW", thumbprint);
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

    [Fact]
    public void TryGetDeviceIdForThumbprint_ForgetsAThumbprintThatWasRepinned()
    {
        var store = new PeerCertificateStore();
        store.Pin("agent-1", "OLD");
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

        store.Load(Path.Combine(Path.GetTempPath(), $"educast-absent-{Guid.NewGuid():N}.json"));

        Assert.False(store.TryGetPinnedThumbprint("agent-1", out _));
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
