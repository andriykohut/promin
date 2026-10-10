using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Promin.Protocol.Discovery;

/// <summary>
/// The datagram a student PC broadcasts so the teacher's console can find it: who it is, what to
/// call it, and where and with what certificate to dial it.
/// </summary>
/// <remarks>
/// <b>An announcement is a claim from the network, and nothing more.</b> Anyone on the LAN can
/// send one, so nothing in it is trusted for pairing. It only says where to dial; whether the PC
/// that answers is the one a person meant is decided by the room code and the pinned certificate
/// (see <see cref="Transport.PeerCertificateStore"/>), never by what an announcement said.
/// <see cref="TryDecode"/> therefore treats its input as hostile and either returns a value that
/// is well-formed, or <c>false</c>.
///
/// <see cref="DeviceId"/> is the certificate's subject name and is for machines only.
/// <see cref="DisplayName"/> is what a person reads; it is cleaned of anything that could be
/// used to mislead or mangle a list (see <see cref="CleanDisplayName"/>).
/// <see cref="CertificateThumbprint"/> is carried so the console can show which certificate it
/// is about to dial, not so that it can believe it.
/// </remarks>
public readonly record struct DiscoveryAnnouncement(
    string DeviceId,
    string DisplayName,
    int QuicPort,
    string CertificateThumbprint)
{
    /// <summary>The longest datagram <see cref="TryDecode"/> will look at.</summary>
    public const int MaxDatagramBytes = 512;

    /// <summary>The longest display name a decoded announcement carries, in UTF-16 characters.</summary>
    public const int MaxDisplayNameLength = 64;

    /// <summary>How much of the device id stands in for a display name that has nothing left in it.</summary>
    private const int FallbackNameLength = 8;

    private const string Magic = "PROMIN-ANNOUNCE-V1";

    // The relaxed encoder keeps common non-ASCII text (accented letters, CJK) as UTF-8 rather
    // than \uXXXX escapes, which keeps real names small. It still escapes characters outside
    // the basic plane, so Encode also shortens the name to fit.
    private static readonly JsonSerializerOptions EncodeOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly JsonDocumentOptions DecodeOptions = new() { MaxDepth = 4 };

    /// <summary>
    /// The datagram to send. The display name is cleaned exactly as a receiver would clean it,
    /// and shortened further if that is what it takes to stay within
    /// <see cref="MaxDatagramBytes"/>, since a receiver would refuse a bigger datagram outright.
    /// </summary>
    public byte[] Encode()
    {
        var displayName = CleanDisplayName(DisplayName, DeviceId);
        while (true)
        {
            var encoded = JsonSerializer.SerializeToUtf8Bytes(
                new
                {
                    Magic,
                    DeviceId,
                    DisplayName = displayName,
                    QuicPort,
                    CertificateThumbprint,
                },
                EncodeOptions);

            if (encoded.Length <= MaxDatagramBytes || displayName.Length == 0)
            {
                return encoded;
            }

            // Drop the last character (both halves of a surrogate pair).
            var drop = displayName.Length > 1 && char.IsLowSurrogate(displayName[^1]) ? 2 : 1;
            displayName = displayName[..^drop];
        }
    }

    /// <summary>
    /// Reads a datagram from the network. Returns <c>false</c>, and <c>default</c> in
    /// <paramref name="result"/>, for anything that is not a well-formed announcement: over
    /// <see cref="MaxDatagramBytes"/> bytes, not JSON, the wrong shape, a device id that is not
    /// exactly 32 lowercase hex characters, a port outside 1-65535, or a thumbprint that is not
    /// 40 or 64 hex characters. The thumbprint is returned in upper case, whatever case was sent,
    /// so two announcements from one device are equal. Never throws on any input.
    /// </summary>
    public static bool TryDecode(ReadOnlySpan<byte> bytes, out DiscoveryAnnouncement result)
    {
        result = default;

        if (bytes.IsEmpty || bytes.Length > MaxDatagramBytes)
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(bytes.ToArray(), DecodeOptions);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (!TryGetString(root, "Magic", out var magic) || magic != Magic
                || !TryGetString(root, "DeviceId", out var deviceId) || !Transport.DeviceIdentity.IsValidDeviceId(deviceId)
                || !TryGetString(root, "DisplayName", out var displayName)
                || !TryGetString(root, "CertificateThumbprint", out var thumbprint) || !IsThumbprint(thumbprint)
                || !root.TryGetProperty("QuicPort", out var portElement)
                || portElement.ValueKind != JsonValueKind.Number
                || !portElement.TryGetInt32(out var quicPort)
                || quicPort is < 1 or > 65535)
            {
                return false;
            }

            result = new DiscoveryAnnouncement(
                deviceId, CleanDisplayName(displayName, deviceId), quicPort, thumbprint.ToUpperInvariant());
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            // A string with a lone surrogate escape cannot be materialised.
            return false;
        }
    }

    /// <summary>
    /// Makes a name safe to put in a list a person reads: control characters, characters that
    /// reorder or hide text (formatting characters such as the right-to-left override, and line
    /// and paragraph separators) are removed, the ends are trimmed, and it is cut to
    /// <see cref="MaxDisplayNameLength"/> characters without splitting a surrogate pair. A name
    /// with nothing left stands in as the first 8 characters of the device id.
    /// </summary>
    internal static string CleanDisplayName(string? name, string? deviceId)
    {
        var cleaned = new StringBuilder(Math.Min(name?.Length ?? 0, 256));
        foreach (var rune in (name ?? string.Empty).EnumerateRunes())
        {
            switch (Rune.GetUnicodeCategory(rune))
            {
                case UnicodeCategory.Control:
                case UnicodeCategory.Format:
                case UnicodeCategory.LineSeparator:
                case UnicodeCategory.ParagraphSeparator:
                    continue;
            }

            cleaned.Append(rune.ToString());
        }

        var text = cleaned.ToString().Trim();
        if (text.Length > MaxDisplayNameLength)
        {
            var length = char.IsHighSurrogate(text[MaxDisplayNameLength - 1])
                ? MaxDisplayNameLength - 1
                : MaxDisplayNameLength;
            text = text[..length].TrimEnd();
        }

        if (text.Length > 0)
        {
            return text;
        }

        var id = deviceId ?? string.Empty;
        return id[..Math.Min(FallbackNameLength, id.Length)];
    }

    private static bool IsThumbprint(string? value) =>
        value is { Length: 40 or 64 } && value.All(Uri.IsHexDigit);

    private static bool TryGetString(JsonElement root, string name, out string value)
    {
        value = string.Empty;
        if (!root.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = element.GetString() ?? string.Empty;
        return true;
    }
}
