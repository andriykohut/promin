using Promin.Master.Core.Rooms;

namespace Promin.Master.Core.Wall;

/// <summary>One connection to one agent PC, as <see cref="RoomConnections{TConnection}"/> holds it.</summary>
public interface IPcConnection : IAsyncDisposable
{
    /// <summary>The next screen picture: JPEG bytes, or empty when the PC's screen is not available. Throws once the connection has ended.</summary>
    ValueTask<byte[]> ReceivePictureAsync(CancellationToken ct);
}

/// <summary>What the wall uses of the room connections.</summary>
public interface IRoomConnections
{
    /// <summary>Raised on any thread with a PC's id and its newest picture (empty: screen not available).</summary>
    event Action<string, byte[]>? PictureArrived;

    /// <summary>Raised on any thread when a PC's connection has ended or been dropped.</summary>
    event Action<string>? Ended;

    /// <summary>The PC is online at this address: dial it, unless it is connected, being dialled, or was tried less than five seconds ago.</summary>
    void Want(RoomMember pc);

    /// <summary>Closes the PC's connection, if any. The next <see cref="Want"/> dials again at once.</summary>
    void Drop(string deviceId);
}

/// <summary>
/// One standing connection to each online agent PC: dialled when the wall says the PC is online, kept until it
/// ends, is dropped, or the console closes. The PCs' screen pictures arrive on it, and the broadcast runs over it
/// (<see cref="GetAsync"/>) instead of dialling.
/// </summary>
/// <remarks>
/// A failed dial is reported (for the log) once, until the PC has been reached again: a PC that refuses this
/// console is tried every five seconds for as long as it announces itself, and the log is not capped.
/// </remarks>
public sealed class RoomConnections<TConnection> : IRoomConnections, IAsyncDisposable
    where TConnection : class, IPcConnection
{
    public static readonly TimeSpan DialGap = TimeSpan.FromSeconds(5);

    // Closing a connection to a PC that has gone silent lasts until QUIC gives up on it; nothing waits that long.
    private static readonly TimeSpan CloseWait = TimeSpan.FromSeconds(1);

    private readonly Func<RoomMember, CancellationToken, Task<TConnection>> _dial;
    private readonly Func<DateTimeOffset> _now;
    private readonly Action<string, Exception> _dialFailed;
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private bool _closed;

    public RoomConnections(
        Func<RoomMember, CancellationToken, Task<TConnection>> dial, Func<DateTimeOffset> now, Action<string, Exception> dialFailed)
    {
        ArgumentNullException.ThrowIfNull(dial);
        ArgumentNullException.ThrowIfNull(now);
        ArgumentNullException.ThrowIfNull(dialFailed);
        _dial = dial;
        _now = now;
        _dialFailed = dialFailed;
    }

    public event Action<string, byte[]>? PictureArrived;

    public event Action<string>? Ended;

    public void Want(RoomMember pc)
    {
        ArgumentNullException.ThrowIfNull(pc);
        lock (_gate)
        {
            if (_closed)
            {
                return;
            }

            if (_entries.TryGetValue(pc.DeviceId, out var entry)
                && (entry.Connection is not null || entry.Dialling is not null || _now() - entry.TriedAt < DialGap))
            {
                return;
            }

            StartDial(pc, entry);
        }
    }

    /// <summary>
    /// For the broadcast: the PC's open connection, the dial already in progress, or a dial started now whatever the
    /// gap. Null when it cannot be reached.
    /// </summary>
    public async Task<TConnection?> GetAsync(RoomMember pc, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(pc);
        Task<TConnection?> pending;
        lock (_gate)
        {
            if (_closed)
            {
                return null;
            }

            _entries.TryGetValue(pc.DeviceId, out var entry);
            if (entry?.Connection is { } open)
            {
                return open;
            }

            pending = entry?.Dialling ?? StartDial(pc, entry);
        }

        return await pending.WaitAsync(ct).ConfigureAwait(false);
    }

    public void Drop(string deviceId)
    {
        Entry? entry;
        lock (_gate)
        {
            _entries.Remove(deviceId, out entry);
        }

        if (entry is null)
        {
            return;
        }

        entry.Cts.Cancel();
        if (entry.Connection is { } connection)
        {
            _ = CloseAsync(connection);
        }

        Ended?.Invoke(deviceId);
    }

    public async ValueTask DisposeAsync()
    {
        List<Entry> all;
        lock (_gate)
        {
            if (_closed)
            {
                return;
            }

            _closed = true;
            all = [.. _entries.Values];
            _entries.Clear();
        }

        foreach (var entry in all)
        {
            entry.Cts.Cancel();
        }

        await Task.WhenAll(all.Select(e => e.Connection).OfType<TConnection>().Select(CloseAsync)).ConfigureAwait(false);
    }

    // Under _gate. The entry is in the dictionary, and Dialling set, before the dial can finish.
    private Task<TConnection?> StartDial(RoomMember pc, Entry? entry)
    {
        entry ??= new Entry();
        _entries[pc.DeviceId] = entry;
        entry.TriedAt = _now();
        var dialled = new TaskCompletionSource<TConnection?>(TaskCreationOptions.RunContinuationsAsynchronously);
        entry.Dialling = dialled.Task;
        _ = DialAsync(pc, entry, dialled);
        return dialled.Task;
    }

    private async Task DialAsync(RoomMember pc, Entry entry, TaskCompletionSource<TConnection?> dialled)
    {
        TConnection? connection = null;
        Exception? failure = null;
        try
        {
            connection = await _dial(pc, entry.Cts.Token).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            failure = e;
        }

        bool current;
        var report = false;
        lock (_gate)
        {
            current = _entries.TryGetValue(pc.DeviceId, out var now) && ReferenceEquals(now, entry);
            entry.Dialling = null;
            if (current && connection is not null)
            {
                entry.Connection = connection;
                entry.Failing = false;
            }
            else if (current && failure is not null && !entry.Failing)
            {
                entry.Failing = report = true;
            }
        }

        if (connection is not null && !current)
        {
            // Dropped, or the room connections closed, while it was dialling: not kept.
            _ = CloseAsync(connection);
            connection = null;
        }

        if (report)
        {
            _dialFailed(pc.DeviceId, failure!);
        }

        if (connection is not null)
        {
            _ = Task.Run(() => ReadAsync(pc.DeviceId, entry, connection));
        }

        dialled.TrySetResult(connection);
    }

    private async Task ReadAsync(string deviceId, Entry entry, TConnection connection)
    {
        try
        {
            while (true)
            {
                var picture = await connection.ReceivePictureAsync(entry.Cts.Token).ConfigureAwait(false);
                PictureArrived?.Invoke(deviceId, picture);
            }
        }
        catch (Exception)
        {
            // The connection ended, or was dropped: nothing more comes on it.
        }

        bool current;
        lock (_gate)
        {
            current = _entries.TryGetValue(deviceId, out var now) && ReferenceEquals(now, entry) && ReferenceEquals(entry.Connection, connection);
            if (current)
            {
                entry.Connection = null;
            }
        }

        if (current)
        {
            await CloseAsync(connection).ConfigureAwait(false);
            Ended?.Invoke(deviceId);
        }
    }

    /// <summary>Best effort and bounded: closing is how a PC is told its connection is over. Never throws.</summary>
    private static async Task CloseAsync(TConnection connection)
    {
        try
        {
            await connection.DisposeAsync().AsTask().WaitAsync(CloseWait).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Gone already, or still closing in the background: either way nothing more is read from it.
        }
    }

    private sealed class Entry
    {
        public CancellationTokenSource Cts { get; } = new();
        public DateTimeOffset TriedAt { get; set; }
        public Task<TConnection?>? Dialling { get; set; }
        public TConnection? Connection { get; set; }
        public bool Failing { get; set; }
    }
}
