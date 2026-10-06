using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace Educast.Protocol.Transport;

/// <summary>
/// Certificate pins, keyed by device id. Every peer — master or agent — is pinned under its own
/// device id, so one agent can be paired with several masters and a certificate only ever
/// authenticates the single device it was pinned for.
/// </summary>
/// <remarks>
/// <b>Who may pin.</b> <see cref="Pin"/> is internal on purpose: the only thing allowed to
/// create a pin is the transport's pairing operation
/// (<see cref="QuicMasterConnector.PairAsync"/> / <see cref="QuicAgentListener.AcceptPairingAsync"/>),
/// which calls it on a successful mutual room-code handshake and on nothing else. "Pin only
/// on success" was previously prose in a doc comment that every later caller had to re-implement
/// correctly; it is now a property of the code. Removing a pin is an operator action, so
/// <see cref="Unpin"/> is public.
///
/// <b>A pin is never silently replaced.</b> Re-pinning a device id to a different certificate
/// would let a pairing window re-key a machine that is already trusted, so it throws. Re-pairing
/// a genuinely reimaged machine is therefore an explicit <see cref="Unpin"/> first — a step an
/// operator can be shown and asked about.
///
/// Thumbprints are SHA-256 hex (see <see cref="ComputeThumbprint"/>). SHA-1, which
/// <see cref="X509Certificate.GetCertHashString()"/> returns by default, is a chosen-prefix
/// collision away from letting an attacker mint a second certificate that matches a pin.
///
/// The TLS validation callbacks on both sides run on arbitrary threads and, on the master, for
/// many connections at once, so every operation here is guarded.
/// </remarks>
public sealed class PeerCertificateStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, string> _pinnedThumbprints = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _deviceIdsByThumbprint = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The SHA-256 hex thumbprint this store pins a certificate by.</summary>
    public static string ComputeThumbprint(X509Certificate certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        return certificate.GetCertHashString(HashAlgorithmName.SHA256);
    }

    /// <summary>
    /// Records the certificate a device authenticates with. Pinning the value a device already
    /// has is idempotent.
    /// </summary>
    /// <exception cref="PinConflictException">
    /// A different certificate is already pinned for this device id, or this certificate is
    /// already pinned for a different device id. Either is an explicit <see cref="Unpin"/> away
    /// from being allowed, and neither may happen silently.
    /// </exception>
    internal void Pin(string deviceId, string thumbprint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(thumbprint);

        lock (_gate)
        {
            if (_pinnedThumbprints.TryGetValue(deviceId, out var existing))
            {
                if (string.Equals(existing, thumbprint, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                throw new PinConflictException(
                    $"Device '{deviceId}' is already pinned to a different certificate. Unpin it " +
                    "explicitly before pairing it again — a pin is not replaced silently.");
            }

            if (_deviceIdsByThumbprint.TryGetValue(thumbprint, out var owner))
            {
                throw new PinConflictException(
                    $"That certificate is already pinned for device '{owner}'. One certificate " +
                    "identifies one device.");
            }

            _pinnedThumbprints[deviceId] = thumbprint;
            _deviceIdsByThumbprint[thumbprint] = deviceId;
        }
    }

    /// <summary>
    /// Forgets a device's pin, which is what an operator does before re-pairing a machine that
    /// was reimaged or re-keyed. Returns false if the device was not pinned.
    /// </summary>
    public bool Unpin(string deviceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);

        lock (_gate)
        {
            if (!_pinnedThumbprints.Remove(deviceId, out var thumbprint))
            {
                return false;
            }

            _deviceIdsByThumbprint.Remove(thumbprint);
            return true;
        }
    }

    /// <summary>How many devices are pinned.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _pinnedThumbprints.Count;
            }
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

    /// <summary>
    /// Writes the pins to <paramref name="path"/>. This file is the entire trust state of the
    /// device, so the write is atomic: a full temporary file is created first and then swapped
    /// in, which means a crash or a full disk mid-write leaves the previous file intact rather
    /// than a truncated one that loses every pairing on the machine.
    /// </summary>
    public void Save(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        Dictionary<string, string> snapshot;
        lock (_gate)
        {
            snapshot = new Dictionary<string, string>(_pinnedThumbprints, StringComparer.Ordinal);
        }

        AtomicFile.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(snapshot));
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
                if (string.IsNullOrWhiteSpace(deviceId)
                    || string.IsNullOrWhiteSpace(thumbprint)
                    || _deviceIdsByThumbprint.ContainsKey(thumbprint))
                {
                    continue;
                }

                _pinnedThumbprints[deviceId] = thumbprint;
                _deviceIdsByThumbprint[thumbprint] = deviceId;
            }
        }
    }
}
