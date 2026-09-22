using System.Security.Cryptography;

namespace Educast.Protocol.Pairing;

public static class PairingCrypto
{
    public static byte[] ComputeResponse(byte[] presharedKey, byte[] nonce)
    {
        using var hmac = new HMACSHA256(presharedKey);
        return hmac.ComputeHash(nonce);
    }

    public static bool VerifyResponse(byte[] presharedKey, byte[] nonce, byte[] response)
    {
        var expected = ComputeResponse(presharedKey, nonce);
        return CryptographicOperations.FixedTimeEquals(expected, response);
    }
}
