using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Promin.Protocol.Transport;

namespace Promin.Protocol.Tests.Transport;

public class SelfSignedCertificateFactoryTests
{
    /// <summary>
    /// An ECDSA key signs; it cannot encipher a key. KeyEncipherment on an ECDSA certificate is
    /// meaningless, and as a critical extension it is something a strict TLS stack is entitled
    /// to refuse. Fails the certificate that asserted DigitalSignature | KeyEncipherment.
    /// </summary>
    [Fact]
    public void Create_AssertsDigitalSignatureOnly_AsItsKeyUsage()
    {
        using var certificate = SelfSignedCertificateFactory.Create("agent-1");

        var keyUsage = Assert.Single(certificate.Extensions.OfType<X509KeyUsageExtension>());

        Assert.Equal(X509KeyUsageFlags.DigitalSignature, keyUsage.KeyUsages);
        Assert.True(keyUsage.Critical);
    }

    /// <summary>
    /// The device key never needs to leave the key container, so it must not be exportable.
    /// Fails a certificate loaded with X509KeyStorageFlags.Exportable.
    /// </summary>
    [Fact]
    public void Create_ProducesAPrivateKeyThatCannotBeExported()
    {
        using var certificate = SelfSignedCertificateFactory.Create("agent-1");
        using var key = certificate.GetECDsaPrivateKey();

        Assert.NotNull(key);
        Assert.ThrowsAny<CryptographicException>(() => key.ExportPkcs8PrivateKey());
        Assert.ThrowsAny<CryptographicException>(() => key.ExportParameters(includePrivateParameters: true));
    }

    /// <summary>The properties the transport depends on, so tightening the flags cannot quietly lose them.</summary>
    [Fact]
    public void Create_ProducesAUsableEcdsaDeviceCertificate()
    {
        using var certificate = SelfSignedCertificateFactory.Create("agent-1");
        using var key = certificate.GetECDsaPrivateKey();

        Assert.True(certificate.HasPrivateKey);
        Assert.NotNull(key);
        Assert.Equal(256, key.KeySize);
        Assert.Equal("CN=agent-1", certificate.Subject);

        // The key still signs, which is the only thing TLS 1.3 asks of it.
        byte[] message = [1, 2, 3];
        var signature = key.SignData(message, HashAlgorithmName.SHA256);
        using var publicKey = certificate.GetECDsaPublicKey();
        Assert.True(publicKey!.VerifyData(message, signature, HashAlgorithmName.SHA256));

        var usages = Assert.Single(certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>())
            .EnhancedKeyUsages.Cast<Oid>().Select(oid => oid.Value).ToArray();
        Assert.Contains("1.3.6.1.5.5.7.3.1", usages);
        Assert.Contains("1.3.6.1.5.5.7.3.2", usages);
    }
}
