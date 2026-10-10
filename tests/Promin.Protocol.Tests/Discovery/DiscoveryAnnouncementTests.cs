using System.Text;
using Promin.Protocol.Discovery;

namespace Promin.Protocol.Tests.Discovery;

public class DiscoveryAnnouncementTests
{
    internal const string ValidId = "0123456789abcdef0123456789abcdef";
    internal const string ValidThumbprint = "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF";

    /// <summary>
    /// Builds a datagram by hand so a test can put in a value the encoder would never produce.
    /// <paramref name="displayName"/> and <paramref name="quicPort"/> go in as raw JSON text,
    /// so a test can use JSON escapes such as \u0007.
    /// </summary>
    private static byte[] Datagram(string deviceId, string displayName, string quicPort, string thumbprint) =>
        Encoding.UTF8.GetBytes(
            "{\"Magic\":\"PROMIN-ANNOUNCE-V1\"," +
            $"\"DeviceId\":\"{deviceId}\",\"DisplayName\":\"{displayName}\"," +
            $"\"QuicPort\":{quicPort},\"CertificateThumbprint\":\"{thumbprint}\"}}");

    [Fact]
    public void EncodeThenDecode_RoundTrips()
    {
        var original = new DiscoveryAnnouncement(ValidId, "PC-04", QuicPort: 17843, ValidThumbprint);

        var encoded = original.Encode();
        var decoded = DiscoveryAnnouncement.TryDecode(encoded, out var result);

        Assert.True(decoded);
        Assert.Equal(original, result);
    }

    [Fact]
    public void EncodeThenDecode_RoundTripsAThumbprintOfSha1Length()
    {
        var original = new DiscoveryAnnouncement(ValidId, "PC-04", 17843, ValidThumbprint[..40]);

        Assert.True(DiscoveryAnnouncement.TryDecode(original.Encode(), out var result));
        Assert.Equal(original, result);
    }

    [Fact]
    public void TryDecode_NormalisesTheThumbprintToUpperCase_SoOneDevicesAnnouncementsAreEqual()
    {
        var lower = DiscoveryAnnouncement.TryDecode(
            Datagram(ValidId, "PC-04", "17843", ValidThumbprint.ToLowerInvariant()), out var fromLower);
        var upper = DiscoveryAnnouncement.TryDecode(
            Datagram(ValidId, "PC-04", "17843", ValidThumbprint), out var fromUpper);

        Assert.True(lower);
        Assert.True(upper);
        Assert.Equal(ValidThumbprint, fromLower.CertificateThumbprint);
        Assert.Equal(fromUpper, fromLower);
    }

    [Fact]
    public void TryDecode_ReturnsFalse_ForGarbageBytes()
    {
        var decoded = DiscoveryAnnouncement.TryDecode([1, 2, 3], out var result);

        Assert.False(decoded);
        Assert.Equal(default, result);
    }

    [Fact]
    public void TryDecode_RefusesAnOversizedDatagram()
    {
        // Otherwise valid, padded with trailing whitespace (which JSON allows) to 513 bytes.
        var valid = Datagram(ValidId, "PC-04", "17843", ValidThumbprint);
        var oversized = valid.Concat(Enumerable.Repeat((byte)' ', 513 - valid.Length)).ToArray();
        Assert.Equal(513, oversized.Length);
        Assert.True(DiscoveryAnnouncement.TryDecode(valid, out _));

        Assert.False(DiscoveryAnnouncement.TryDecode(oversized, out var result));
        Assert.Equal(default, result);

        // Exactly 512 is still accepted: the limit is on the size, not a wish to refuse padding.
        var atTheLimit = valid.Concat(Enumerable.Repeat((byte)' ', 512 - valid.Length)).ToArray();
        Assert.True(DiscoveryAnnouncement.TryDecode(atTheLimit, out _));
    }

    [Theory]
    [InlineData("agent-1")]
    [InlineData("0123456789abcdef0123456789abcde")]        // 31 hex characters
    [InlineData("0123456789abcdef0123456789abcdef0")]      // 33 hex characters
    [InlineData("0123456789ABCDEF0123456789ABCDEF")]       // 32 uppercase hex characters
    [InlineData("0123456789abcdef0123456789abcdeg")]       // 32 characters, one not hex
    [InlineData("")]
    public void TryDecode_RefusesAMalformedDeviceId(string deviceId)
    {
        var decoded = DiscoveryAnnouncement.TryDecode(
            Datagram(deviceId, "PC-04", "17843", ValidThumbprint), out var result);

        Assert.False(decoded);
        Assert.Equal(default, result);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("65536")]
    [InlineData("-1")]
    [InlineData("1.5")]
    [InlineData("\"17843\"")]
    [InlineData("99999999999999999999")]
    public void TryDecode_RefusesAPortOutOfRange(string port)
    {
        var decoded = DiscoveryAnnouncement.TryDecode(
            Datagram(ValidId, "PC-04", port, ValidThumbprint), out var result);

        Assert.False(decoded);
        Assert.Equal(default, result);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(65535)]
    public void TryDecode_AcceptsThePortsAtEitherEdge(int port)
    {
        Assert.True(DiscoveryAnnouncement.TryDecode(
            Datagram(ValidId, "PC-04", port.ToString(), ValidThumbprint), out var result));
        Assert.Equal(port, result.QuicPort);
    }

    [Theory]
    [InlineData("")]
    [InlineData("0123456789ABCDEF0123456789ABCDEF012345678")]      // 41
    [InlineData("0123456789ABCDEF0123456789ABCDEF0123456")]        // 39
    [InlineData("0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDE")]   // 63
    [InlineData("0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEFA")] // 65
    [InlineData("0123456789ABCDEF0123456789ABCDEF0123456G")]       // 40, not hex
    public void TryDecode_RefusesAMalformedThumbprint(string thumbprint)
    {
        var decoded = DiscoveryAnnouncement.TryDecode(
            Datagram(ValidId, "PC-04", "17843", thumbprint), out var result);

        Assert.False(decoded);
        Assert.Equal(default, result);
    }

    [Fact]
    public void TryDecode_CleansTheDisplayName()
    {
        Assert.True(DiscoveryAnnouncement.TryDecode(
            Datagram(ValidId, "  PC-04\\u0007\\n ", "17843", ValidThumbprint), out var cleaned));
        Assert.Equal("PC-04", cleaned.DisplayName);

        Assert.True(DiscoveryAnnouncement.TryDecode(
            Datagram(ValidId, new string('x', 100), "17843", ValidThumbprint), out var cut));
        Assert.Equal(new string('x', 64), cut.DisplayName);

        Assert.True(DiscoveryAnnouncement.TryDecode(
            Datagram(ValidId, "", "17843", ValidThumbprint), out var empty));
        Assert.Equal(ValidId[..8], empty.DisplayName);
    }

    [Theory]
    [InlineData("\\u0007\\n\\t")]            // nothing but control characters
    [InlineData("   ")]                      // nothing but spaces
    [InlineData("\\u202e\\u200b")]           // nothing but invisible formatting characters
    public void TryDecode_UsesTheIdWhenNothingIsLeftOfTheName(string name)
    {
        Assert.True(DiscoveryAnnouncement.TryDecode(
            Datagram(ValidId, name, "17843", ValidThumbprint), out var result));
        Assert.Equal(ValidId[..8], result.DisplayName);
    }

    [Fact]
    public void TryDecode_RemovesTheCharactersThatReorderOrHideText()
    {
        // U+202E (right-to-left override) would make later text read backwards.
        Assert.True(DiscoveryAnnouncement.TryDecode(
            Datagram(ValidId, "PC\\u202e-04\\u2028", "17843", ValidThumbprint), out var result));
        Assert.Equal("PC-04", result.DisplayName);
    }

    [Fact]
    public void TryDecode_DoesNotCutASurrogatePairInHalf()
    {
        var name = new string('x', 63) + "\U0001F600";   // the emoji straddles the 64th character
        var original = new DiscoveryAnnouncement(ValidId, name, 17843, ValidThumbprint);

        Assert.True(DiscoveryAnnouncement.TryDecode(original.Encode(), out var result));
        Assert.Equal(new string('x', 63), result.DisplayName);
    }

    [Fact]
    public void Encode_StaysInsideTheLimitEvenForTheLongestName()
    {
        // Worst case for size: 64 UTF-16 characters that each cost the most bytes. The encoder
        // writes one emoji as twelve bytes of escapes, so the name has to be trimmed to fit.
        var name = string.Concat(Enumerable.Repeat("\U0001F600", 32));
        var original = new DiscoveryAnnouncement(ValidId, name, 65535, ValidThumbprint);

        var encoded = original.Encode();

        Assert.True(encoded.Length <= 512, $"encoded to {encoded.Length} bytes");
        Assert.True(DiscoveryAnnouncement.TryDecode(encoded, out var result));
        Assert.StartsWith(result.DisplayName, name);
        Assert.NotEmpty(result.DisplayName);
        Assert.False(char.IsHighSurrogate(result.DisplayName[^1]));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("\"PROMIN-ANNOUNCE-V1\"")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"Magic\":\"something-else\"}")]
    [InlineData("{\"Magic\":42,\"DeviceId\":1}")]
    public void TryDecode_RefusesJsonThatIsNotAnAnnouncement(string text)
    {
        Assert.False(DiscoveryAnnouncement.TryDecode(Encoding.UTF8.GetBytes(text), out var result));
        Assert.Equal(default, result);
    }

    [Fact]
    public void TryDecode_RefusesAnAnnouncementWithTheWrongMagic()
    {
        var text = Encoding.UTF8.GetString(Datagram(ValidId, "PC-04", "17843", ValidThumbprint))
            .Replace("PROMIN-ANNOUNCE-V1", "PROMIN-ANNOUNCE-V2");

        Assert.False(DiscoveryAnnouncement.TryDecode(Encoding.UTF8.GetBytes(text), out _));
    }

    [Fact]
    public void TryDecode_RefusesAFieldThatIsNotTheRightKindOfJsonValue()
    {
        // DisplayName as a number, DeviceId as an object: must be a clean false, not an exception.
        var text = "{\"Magic\":\"PROMIN-ANNOUNCE-V1\",\"DeviceId\":{},\"DisplayName\":5,\"QuicPort\":1,\"CertificateThumbprint\":[]}";

        Assert.False(DiscoveryAnnouncement.TryDecode(Encoding.UTF8.GetBytes(text), out _));
    }

    [Fact]
    public void TryDecode_RefusesDeeplyNestedJsonWithoutThrowing()
    {
        var nested = new string('[', 400) + new string(']', 400);

        Assert.False(DiscoveryAnnouncement.TryDecode(Encoding.UTF8.GetBytes(nested), out _));
    }

    [Fact]
    public void TryDecode_RefusesBytesThatAreNotUtf8()
    {
        var bytes = Datagram(ValidId, "PC-04", "17843", ValidThumbprint);
        bytes[bytes.Length / 2] = 0xFF;

        Assert.False(DiscoveryAnnouncement.TryDecode(bytes, out _));
    }

    [Fact]
    public void TryDecode_RefusesAnEmptyDatagram()
    {
        Assert.False(DiscoveryAnnouncement.TryDecode([], out _));
    }
}
