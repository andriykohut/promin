using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Educast.Protocol.Transport;

namespace Educast.Protocol.Tests.Transport;

/// <summary>
/// The permission to let one certificate through TLS on a pairing connection. It is explicit,
/// single-use, bounded, checks the name, never re-keys a device that is already pinned — and
/// does let the same certificate pair again.
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
        Assert.True(window.HasAdmitted);
        Assert.False(window.TryAdmit(second));
        Assert.False(window.TryAdmit(first));
    }

    /// <summary>
    /// The first certificate consumes the window even when it is the one refused — and the
    /// operation waiting on the window is told at once, with the reason, rather than left to
    /// wait for a master it can no longer let in.
    /// </summary>
    [Fact]
    public async Task TryAdmit_IsConsumedByTheFirstCertificate_EvenWhenThatOneIsRefused()
    {
        using var presented = SelfSignedCertificateFactory.Create("agent-1");
        using var later = SelfSignedCertificateFactory.Create("agent-1");
        var pins = new PeerCertificateStore();
        pins.Pin("agent-1", new string('A', 64));
        var window = new PairingTrustWindow(pins, "agent-1", Generous);

        Assert.False(window.TryAdmit(presented));
        Assert.False(window.HasAdmitted);
        Assert.Contains("unpin", window.RefusalReason);

        // Now nothing stands in the way of the second certificate except the window being spent.
        pins.Unpin("agent-1");
        Assert.False(window.TryAdmit(later));

        var spent = await Assert.ThrowsAsync<PairingWindowSpentException>(() => window.Claimed);
        Assert.Equal(window.RefusalReason, spent.Message);
    }

    [Fact]
    public async Task TryAdmit_Refuses_OnceTheLifetimeHasRunOut()
    {
        using var certificate = SelfSignedCertificateFactory.Create("agent-1");
        var window = new PairingTrustWindow(
            new PeerCertificateStore(), "agent-1", TimeSpan.FromMilliseconds(50));

        await Task.Delay(200);

        Assert.False(window.TryAdmit(certificate));

        // An expired window was not "spent" by anyone: nothing is reported as refused.
        Assert.Null(window.RefusalReason);
        Assert.False(window.Claimed.IsCompleted);
    }

    [Fact]
    public void TryAdmit_Refuses_OnceTheOperationHasClosedTheWindow()
    {
        using var certificate = SelfSignedCertificateFactory.Create("agent-1");
        var window = new PairingTrustWindow(new PeerCertificateStore(), "agent-1", Generous);

        window.Close();

        Assert.False(window.TryAdmit(certificate));
        Assert.Null(window.RefusalReason);
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
    /// The master checks the name it dialled: a certificate naming any other device is refused
    /// before a handshake message is exchanged. Fails a window that binds the device id only by
    /// looking at the pins, which lets whatever answers at an address be paired under whatever
    /// name the operator typed.
    /// </summary>
    [Fact]
    public void TryAdmit_OnTheMaster_Refuses_ACertificateThatNamesAnotherDevice()
    {
        using var presented = SelfSignedCertificateFactory.Create("agent-3");
        var window = new PairingTrustWindow(new PeerCertificateStore(), "agent-7", Generous);

        Assert.False(window.TryAdmit(presented));
        Assert.Contains("'agent-3'", window.RefusalReason);
        Assert.Contains("'agent-7'", window.RefusalReason);
    }

    /// <summary>
    /// A device that is already pinned to a different certificate is not re-keyed by a pairing
    /// window; the reason says to unpin, which here is the remedy.
    /// </summary>
    [Fact]
    public void TryAdmit_Refuses_ADeviceAlreadyPinnedToADifferentCertificate()
    {
        using var presented = SelfSignedCertificateFactory.Create("agent-3");
        var pins = new PeerCertificateStore();
        pins.Pin("agent-3", new string('A', 64));
        var window = new PairingTrustWindow(pins, "agent-3", Generous);

        Assert.False(window.TryAdmit(presented));
        Assert.Contains("unpin", window.RefusalReason);
    }

    /// <summary>
    /// The same certificate for the same device is not a re-key: it is a re-pair, and it is how
    /// a pairing that only one side recorded gets finished. Fails a window that refuses every
    /// device that is already pinned.
    /// </summary>
    [Fact]
    public void TryAdmit_Admits_TheSameCertificateForADeviceThatIsAlreadyPinnedToIt()
    {
        using var presented = SelfSignedCertificateFactory.Create("agent-3");
        var pins = new PeerCertificateStore();
        pins.Pin("agent-3", PeerCertificateStore.ComputeThumbprint(presented));

        Assert.True(new PairingTrustWindow(pins, "agent-3", Generous).TryAdmit(presented));
        Assert.True(new PairingTrustWindow(pins, expectedDeviceId: null, Generous).TryAdmit(presented));
    }

    /// <summary>One certificate identifies one device: it cannot be paired under a second name.</summary>
    [Fact]
    public void TryAdmit_Refuses_ACertificateAlreadyPinnedForAnotherDevice()
    {
        using var presented = SelfSignedCertificateFactory.Create("agent-7");
        var pins = new PeerCertificateStore();
        pins.Pin("agent-3", PeerCertificateStore.ComputeThumbprint(presented));
        var window = new PairingTrustWindow(pins, "agent-7", Generous);

        Assert.False(window.TryAdmit(presented));
        Assert.Contains("'agent-3'", window.RefusalReason);
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
    public void TryAdmit_Refuses_ACertificateThatNamesNoDevice()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("O=Nameless", key, HashAlgorithmName.SHA256);
        using var nameless = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));

        Assert.Null(CertificateIdentity.ReadDeviceId(nameless));
        Assert.False(
            new PairingTrustWindow(new PeerCertificateStore(), expectedDeviceId: null, Generous).TryAdmit(nameless));
    }

    // ---- Who the window admits: anyone, or pinned peers only ---------------------------------------

    /// <summary>
    /// A window for pinned peers only refuses a certificate that is not pinned, and a refusal like that does
    /// not use the window up: the pinned peer that comes after it is still admitted, and nothing was reported
    /// as refused to the operation waiting on the window.
    /// </summary>
    [Fact]
    public void PinnedPeersOnly_RefusesAStranger_WithoutConsumingTheWindow()
    {
        using var stranger = SelfSignedCertificateFactory.Create("master-stranger");
        using var pinned = SelfSignedCertificateFactory.Create("master-1");
        var pins = new PeerCertificateStore();
        pins.Pin("master-1", PeerCertificateStore.ComputeThumbprint(pinned));
        var window = new PairingTrustWindow(pins, expectedDeviceId: null, Generous, PairingAdmission.PinnedPeersOnly);

        Assert.False(window.TryAdmit(stranger));
        Assert.False(window.TryAdmit(stranger));
        Assert.False(window.HasAdmitted);
        Assert.Null(window.RefusalReason);
        Assert.False(window.Claimed.IsCompleted);

        Assert.True(window.TryAdmit(pinned));
        Assert.True(window.HasAdmitted);
    }

    [Fact]
    public void PinnedPeersOnly_StillAdmitsOnlyOneCertificate()
    {
        using var pinned = SelfSignedCertificateFactory.Create("master-1");
        var pins = new PeerCertificateStore();
        pins.Pin("master-1", PeerCertificateStore.ComputeThumbprint(pinned));
        var window = new PairingTrustWindow(pins, expectedDeviceId: null, Generous, PairingAdmission.PinnedPeersOnly);

        Assert.True(window.TryAdmit(pinned));
        Assert.False(window.TryAdmit(pinned));
    }

    /// <summary>
    /// A new certificate under the name of a pinned master is a stranger as well: the pin is the certificate,
    /// not the name.
    /// </summary>
    [Fact]
    public void PinnedPeersOnly_RefusesANewCertificateUnderAPinnedName()
    {
        using var impostor = SelfSignedCertificateFactory.Create("master-1");
        var pins = new PeerCertificateStore();
        pins.Pin("master-1", new string('B', 64));
        var window = new PairingTrustWindow(pins, expectedDeviceId: null, Generous, PairingAdmission.PinnedPeersOnly);

        Assert.False(window.TryAdmit(impostor));
        Assert.False(window.HasAdmitted);
        Assert.Null(window.RefusalReason);
    }

    [Fact]
    public void Anyone_IsWhatAWindowDoesWhenNotToldOtherwise()
    {
        using var stranger = SelfSignedCertificateFactory.Create("master-stranger");

        Assert.True(new PairingTrustWindow(new PeerCertificateStore(), expectedDeviceId: null, Generous).TryAdmit(stranger));
    }

    [Fact]
    public void ReadDeviceId_IsTheSubjectCommonName()
    {
        using var certificate = SelfSignedCertificateFactory.Create("classroom-pc-07");

        Assert.Equal("classroom-pc-07", CertificateIdentity.ReadDeviceId(certificate));
    }
}
