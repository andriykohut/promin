using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Educast.Protocol.Transport;

namespace Educast.Protocol.Tests.Transport;

/// <summary>
/// The permission to let one unpinned certificate through TLS. It is explicit, single-use,
/// bounded, and never re-keys a device that is already pinned.
/// </summary>
public class PairingTrustWindowTests
{
    private static readonly TimeSpan Generous = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Fails trust-on-first-use as a mode: there, every certificate is admitted for as long as
    /// the mode is on.
    /// </summary>
    [Fact]
    public void TryAdmit_AdmitsExactlyOneCertificate_Once()
    {
        using var first = SelfSignedCertificateFactory.Create("agent-1");
        using var second = SelfSignedCertificateFactory.Create("agent-1");
        var window = new PairingTrustWindow(new PeerCertificateStore(), "agent-1", Generous);

        Assert.True(window.TryAdmit(first));
        Assert.False(window.TryAdmit(second));
        Assert.False(window.TryAdmit(first));
    }

    /// <summary>The first certificate consumes the window even when it is the one refused.</summary>
    [Fact]
    public void TryAdmit_IsConsumedByTheFirstCertificate_EvenWhenThatOneIsRefused()
    {
        using var presented = SelfSignedCertificateFactory.Create("agent-1");
        using var later = SelfSignedCertificateFactory.Create("agent-2");
        var pins = new PeerCertificateStore();
        pins.Pin("agent-1", new string('A', 64));
        var window = new PairingTrustWindow(pins, "agent-1", Generous);

        Assert.False(window.TryAdmit(presented));
        pins.Unpin("agent-1");
        Assert.False(window.TryAdmit(later));
    }

    [Fact]
    public async Task TryAdmit_Refuses_OnceTheLifetimeHasRunOut()
    {
        using var certificate = SelfSignedCertificateFactory.Create("agent-1");
        var window = new PairingTrustWindow(
            new PeerCertificateStore(), "agent-1", TimeSpan.FromMilliseconds(50));

        await Task.Delay(200);

        Assert.False(window.TryAdmit(certificate));
    }

    [Fact]
    public void TryAdmit_Refuses_OnceTheOperationHasClosedTheWindow()
    {
        using var certificate = SelfSignedCertificateFactory.Create("agent-1");
        var window = new PairingTrustWindow(new PeerCertificateStore(), "agent-1", Generous);

        window.Close();

        Assert.False(window.TryAdmit(certificate));
    }

    [Fact]
    public void TryAdmit_Refuses_ANullCertificate_WithoutConsumingTheWindow()
    {
        using var certificate = SelfSignedCertificateFactory.Create("agent-1");
        var window = new PairingTrustWindow(new PeerCertificateStore(), "agent-1", Generous);

        Assert.False(window.TryAdmit(null));
        Assert.True(window.TryAdmit(certificate));
    }

    /// <summary>
    /// The connector binds the device id it is pairing: a device that is already pinned to a
    /// different certificate is not re-keyed by a pairing window. Fails a window that ignores
    /// the device id, which is how pairing one client could accept a forged certificate for
    /// another that is already trusted.
    /// </summary>
    [Fact]
    public void TryAdmit_Refuses_ADeviceAlreadyPinnedToADifferentCertificate()
    {
        using var presented = SelfSignedCertificateFactory.Create("agent-3");
        var pins = new PeerCertificateStore();
        pins.Pin("agent-3", new string('A', 64));

        Assert.False(new PairingTrustWindow(pins, "agent-3", Generous).TryAdmit(presented));
    }

    /// <summary>One certificate identifies one device: agent-3's cannot be admitted as agent-7.</summary>
    [Fact]
    public void TryAdmit_Refuses_ACertificateAlreadyPinnedForAnotherDevice()
    {
        using var presented = SelfSignedCertificateFactory.Create("agent-3");
        var pins = new PeerCertificateStore();
        pins.Pin("agent-3", PeerCertificateStore.ComputeThumbprint(presented));

        Assert.False(new PairingTrustWindow(pins, "agent-7", Generous).TryAdmit(presented));
    }

    /// <summary>
    /// On the agent there is no expected device id; the certificate's own subject name is the
    /// id it would be pinned under, and the same rule holds for it.
    /// </summary>
    [Fact]
    public void TryAdmit_OnTheAgent_Refuses_AMasterNameAlreadyPinnedToADifferentCertificate()
    {
        using var impostor = SelfSignedCertificateFactory.Create("master-1");
        using var newcomer = SelfSignedCertificateFactory.Create("master-2");
        var pins = new PeerCertificateStore();
        pins.Pin("master-1", new string('B', 64));

        Assert.False(new PairingTrustWindow(pins, expectedDeviceId: null, Generous).TryAdmit(impostor));
        Assert.True(new PairingTrustWindow(pins, expectedDeviceId: null, Generous).TryAdmit(newcomer));
    }

    /// <summary>A certificate that names no device has nothing to be pinned under.</summary>
    [Fact]
    public void TryAdmit_OnTheAgent_Refuses_ACertificateThatNamesNoDevice()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("O=Nameless", key, HashAlgorithmName.SHA256);
        using var nameless = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));

        Assert.Null(CertificateIdentity.ReadDeviceId(nameless));
        Assert.False(
            new PairingTrustWindow(new PeerCertificateStore(), expectedDeviceId: null, Generous).TryAdmit(nameless));
    }

    [Fact]
    public void ReadDeviceId_IsTheSubjectCommonName()
    {
        using var certificate = SelfSignedCertificateFactory.Create("classroom-pc-07");

        Assert.Equal("classroom-pc-07", CertificateIdentity.ReadDeviceId(certificate));
    }
}
