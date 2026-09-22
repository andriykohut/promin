using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace Educast.Protocol.Transport;

/// <summary>
/// Trust-on-first-use certificate pins, keyed by device id. Every peer — master or
/// agent — is pinned under its own device id, so one agent can be paired with several
/// masters and a certificate only ever authenticates the single device it was pinned for.
/// </summary>
/// <remarks>
/// Thumbprints are SHA-256 hex (see <see cref="ComputeThumbprint"/>). SHA-1, which
/// <see cref="X509Certificate2.GetCertHashString()"/> returns by default, is a chosen-prefix
/// collision away from letting an attacker mint a second certificate that matches a pin.
///
/// The TLS validation callbacks on both sides run on arbitrary threads and, on the master,
/// for many connections at once, so every operation here is guarded.
/// </remarks>
public sealed class PeerCertificateStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, string> _pinnedThumbprints = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _deviceIdsByThumbprint = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The SHA-256 hex thumbprint this store pins a certificate by.</summary>
    public static string ComputeThumbprint(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        return certificate.GetCertHashString(System.Security.Cryptography.HashAlgorithmName.SHA256);
    }

    public void Pin(string deviceId, string thumbprint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(thumbprint);

        lock (_gate)
        {
            if (_pinnedThumbprints.TryGetValue(deviceId, out var previous))
            {
                _deviceIdsByThumbprint.Remove(previous);
            }

            _pinnedThumbprints[deviceId] = thumbprint;
            _deviceIdsByThumbprint[thumbprint] = deviceId;
        }
    }

    public bool TryGetPinnedThumbprint(string deviceId, out string? thumbprint)
    {
        lock (_gate)
        {
            return _pinnedThumbprints.TryGetValue(deviceId, out thumbprint);
        }
    }

    /// <summary>
    /// Reverse lookup, used by the agent's TLS callback: a dialling master is identified
    /// only by the certificate it presents, so the pin has to resolve back to a device id.
    /// </summary>
    public bool TryGetDeviceIdForThumbprint(string thumbprint, out string? deviceId)
    {
        lock (_gate)
        {
            return _deviceIdsByThumbprint.TryGetValue(thumbprint, out deviceId);
        }
    }

    public void Save(string path)
    {
        Dictionary<string, string> snapshot;
        lock (_gate)
        {
            snapshot = new Dictionary<string, string>(_pinnedThumbprints, StringComparer.Ordinal);
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, JsonSerializer.Serialize(snapshot));
    }

    /// <summary>
    /// Replaces every pin held in memory with the contents of <paramref name="path"/>.
    /// A missing file leaves the store empty — a device that has never paired has no pins.
    /// </summary>
    public void Load(string path)
    {
        var loaded = File.Exists(path)
            ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) ?? []
            : [];

        lock (_gate)
        {
            _pinnedThumbprints.Clear();
            _deviceIdsByThumbprint.Clear();
            foreach (var (deviceId, thumbprint) in loaded)
            {
                if (string.IsNullOrWhiteSpace(deviceId) || string.IsNullOrWhiteSpace(thumbprint))
                {
                    continue;
                }

                _pinnedThumbprints[deviceId] = thumbprint;
                _deviceIdsByThumbprint[thumbprint] = deviceId;
            }
        }
    }
}
