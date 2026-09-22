using Educast.Protocol.Pairing;

namespace Educast.Protocol.Tests.Pairing;

public class PairingCryptoTests
{
    [Fact]
    public void VerifyResponse_ReturnsTrue_WhenKeyMatches()
    {
        byte[] key = [9, 9, 9, 9];
        byte[] nonce = [1, 2, 3];
        var response = PairingCrypto.ComputeResponse(key, nonce);

        Assert.True(PairingCrypto.VerifyResponse(key, nonce, response));
    }

    [Fact]
    public void VerifyResponse_ReturnsFalse_WhenKeyDoesNotMatch()
    {
        byte[] correctKey = [9, 9, 9, 9];
        byte[] wrongKey = [1, 1, 1, 1];
        byte[] nonce = [1, 2, 3];
        var response = PairingCrypto.ComputeResponse(correctKey, nonce);

        Assert.False(PairingCrypto.VerifyResponse(wrongKey, nonce, response));
    }

    [Fact]
    public void VerifyResponse_ReturnsFalse_WhenResponseIsWrongLength()
    {
        byte[] key = [9, 9, 9, 9];
        byte[] nonce = [1, 2, 3];

        Assert.False(PairingCrypto.VerifyResponse(key, nonce, []));
        Assert.False(PairingCrypto.VerifyResponse(key, nonce, [1, 2, 3]));
    }
}
