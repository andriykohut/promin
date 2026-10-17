using System.Globalization;
using System.Text.Json;
using Promin.Protocol.Pairing;
using Promin.Protocol.Transport;

namespace Promin.Agent.Core;

public enum StripSeverity { Informational, Success, Error }

/// <summary>
/// What the agent window shows: the stored room code and one strip of status or error. A paired PC shows
/// "Paired" even without a code, because pairing has already happened.
/// </summary>
public sealed class AgentWindowModel(AgentPaths paths)
{
    private readonly AgentSettings _settings = new(paths.RoomCodeFile);
    private string? _saveError;

    /// <summary>The stored room code; null until <see cref="Refresh"/> finds a usable one.</summary>
    public string? StoredCode { get; private set; }

    public StripSeverity Severity { get; private set; }

    public string Message { get; private set; } = "";

    /// <summary>Re-reads the room code and pin files. A save error from <see cref="Save"/> stays showing until <see cref="Edited"/>.</summary>
    public void Refresh()
    {
        var lookup = _settings.Lookup();
        StoredCode = lookup.Code;

        if (_saveError is not null)
        {
            (Severity, Message) = (StripSeverity.Error, _saveError);
            return;
        }

        var pins = new PeerCertificateStore();
        try
        {
            pins.Load(paths.MastersFile);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            (Severity, Message) = (StripSeverity.Error, Format(Strings.Status_PairingUnreadable, e.Message));
            return;
        }

        (Severity, Message) =
            pins.Count > 0 ? (StripSeverity.Success, Strings.Status_Paired)
            : lookup.Problem is not null ? (StripSeverity.Error, Strings.Status_CodeUnusable)
            : lookup.Code is null ? (StripSeverity.Informational, Strings.Status_NoCode)
            : (StripSeverity.Informational, Strings.Status_NotPaired);
    }

    /// <summary>
    /// Writes the typed code, spaces and hyphens allowed. Returns false when nothing was written, with the
    /// reason in <see cref="Message"/>.
    /// </summary>
    public bool Save(string typed)
    {
        if (!RoomCode.TryNormalize(typed, out var code))
        {
            _saveError = Strings.Status_BadCode;
            Refresh();
            return false;
        }

        try
        {
            _settings.Save(code);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _saveError = Format(Strings.Status_NotSaved, e.Message);
            Refresh();
            return false;
        }

        _saveError = null;
        Refresh();
        return true;
    }

    /// <summary>The code box changed: a save error stops showing.</summary>
    public void Edited()
    {
        _saveError = null;
        Refresh();
    }

    private static string Format(string template, string argument) =>
        string.Format(CultureInfo.CurrentCulture, template, argument);
}
