using System.Security.Cryptography;
using Promin.Protocol.Transport;

namespace Promin.Protocol.Tests.Transport;

public class CertificateBindingTests
{
    /// <summary>
    /// The exact construction: SHA256(masterCertDER) || SHA256(agentCertDER), 64 bytes, master
    /// first. Recomputed independently so a change to the order, the hash or the encoding fails.
    /// </summary>
    [Fact]
    public void Compute_IsTheTwoCertificateDigests_MasterFirst()
    {
        using var master = SelfSignedCertificateFactory.Create("master-1");
        using var agent = SelfSignedCertificateFactory.Create("agent-1");

        var binding = CertificateBinding.Compute(master, agent);

        Assert.Equal(64, binding.Length);
        Assert.Equal(64, CertificateBinding.Length);
        Assert.Equal(SHA256.HashData(master.RawData), binding[..32]);
        Assert.Equal(SHA256.HashData(agent.RawData), binding[32..]);
    }

    /// <summary>
    /// The two halves are positional: swapping the roles is a different binding, and so is any
    /// other certificate in either place — which is what a relay's certificate is.
    /// </summary>
    [Fact]
    public void Compute_Differs_WhenEitherCertificateOrTheirOrderDiffers()
    {
        using var master = SelfSignedCertificateFactory.Create("master-1");
        using var agent = SelfSignedCertificateFactory.Create("agent-1");
        using var relay = SelfSignedCertificateFactory.Create("agent-1");

        var direct = CertificateBinding.Compute(master, agent);

        Assert.NotEqual(direct, CertificateBinding.Compute(agent, master));
        Assert.NotEqual(direct, CertificateBinding.Compute(master, relay));
        Assert.NotEqual(direct, CertificateBinding.Compute(relay, agent));
        Assert.Equal(direct, CertificateBinding.Compute(master, agent));
    }
}
