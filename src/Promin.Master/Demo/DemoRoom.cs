using System.Collections.Concurrent;
using Promin.Master.Core.Control;
using Promin.Master.Core.Rooms;
using Promin.Master.Core.Sending;
using Promin.Master.Core.Setup;
using Promin.Master.Core.Wall;
using Promin.Protocol.Messages;
using Promin.Protocol.Video;

namespace Promin.Master.Demo;

/// <summary>
/// A made-up room for screenshots, which the console uses in place of the real one when a Debug build starts with
/// <c>--demo</c>. Twelve PCs, PC-01 to PC-12, each show one of the drawn screens in <c>Demo/Screens</c>; PC-12 is
/// switched off. Nothing reaches the network. Locking, unlocking, broadcasting, sending files, shutting down and
/// restarting answer as a healthy room would; controlling a PC is refused.
/// </summary>
internal sealed class DemoRoom
{
    private static readonly string[] ScreenOrder =
        ["doc", "sheet", "slides", "code", "map", "article", "paint", "desktop", "code", "doc", "map", "sheet"];

    private readonly ConcurrentDictionary<string, RoomMember> _switchedOn = new(StringComparer.Ordinal);

    private readonly ConcurrentDictionary<string, bool> _receiving = new(StringComparer.Ordinal);

    public DemoRoom()
    {
        var members = Enumerable.Range(1, 12)
            .Select(i => new RoomMember($"demo-pc-{i:00}", $"PC-{i:00}", $"192.0.2.{10 + i}", 17843, new DateOnly(2026, 11, 2), Selected: false))
            .ToList();
        foreach (var pc in members.SkipLast(1))
        {
            _switchedOn[pc.DeviceId] = pc;
        }

        var screens = members.Select((pc, i) => (pc.DeviceId, Screen: Jpeg(ScreenOrder[i]))).ToDictionary(p => p.DeviceId, p => p.Screen);

        Store = new DemoStore(members);
        Discovery = new DemoDiscovery(_switchedOn);
        Connections = new DemoConnections(_switchedOn, _receiving, screens, broadcastScreen: Jpeg("slides"));
        Broadcast = new DemoBroadcast(_receiving);
    }

    public IRoomStore Store { get; }

    public IDiscovery Discovery { get; }

    public IRoomConnections Connections { get; }

    public IPairingClient Pairing { get; } = new DemoPairing();

    public IBroadcastController Broadcast { get; }

    public IFileSender Files { get; } = new DemoFiles();

    private static byte[] Jpeg(string name)
    {
        using var stream = typeof(DemoRoom).Assembly.GetManifestResourceStream($"demo-screens/{name}.jpg")
            ?? throw new InvalidOperationException($"The demo screen {name}.jpg is not built in.");
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    private sealed class DemoStore(List<RoomMember> members) : IRoomStore
    {
        private IReadOnlyList<RoomMember> _members = members;

        public IReadOnlyList<RoomMember> Load() => _members;

        public void Save(IReadOnlyList<RoomMember> members) => _members = [.. members];
    }

    private sealed class DemoDiscovery(ConcurrentDictionary<string, RoomMember> on) : IDiscovery
    {
        private Timer? _announcements;

        public event Action<AgentPc>? Seen;

        public bool Start()
        {
            _announcements ??= new Timer(_ => Announce(), null, TimeSpan.Zero, TimeSpan.FromSeconds(2));
            return true;
        }

        public void Stop()
        {
            _announcements?.Dispose();
            _announcements = null;
        }

        private void Announce()
        {
            foreach (var pc in on.Values)
            {
                Seen?.Invoke(new AgentPc(pc.DeviceId, pc.DisplayName, pc.LastAddress, pc.Port));
            }
        }
    }

    private sealed class DemoPairing : IPairingClient
    {
        public Task<PairResult> PairAsync(AgentPc pc, string roomCode, CancellationToken ct) => Task.FromResult(PairResult.Paired);

        public void Unpair(string deviceId)
        {
        }

        public bool IsTrusted(string deviceId) => true;
    }

    private sealed class DemoConnections(
        ConcurrentDictionary<string, RoomMember> on,
        ConcurrentDictionary<string, bool> receiving,
        Dictionary<string, byte[]> screens,
        byte[] broadcastScreen) : IRoomConnections
    {
        private static readonly TimeSpan RestartTakes = TimeSpan.FromSeconds(20);

        private readonly ConcurrentDictionary<string, Timer> _open = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, bool> _locked = new(StringComparer.Ordinal);

        public event Action<string, byte[]>? PictureArrived;

        public event Action<string>? Ended;

        public event Action<string, bool>? LockReported;

        public event Action<string, bool>? PowerAnswered;

        public void Want(RoomMember pc)
        {
            var id = pc.DeviceId;
            if (_open.ContainsKey(id) || !on.ContainsKey(id))
            {
                return;
            }

            var pictures = new Timer(
                _ => PictureArrived?.Invoke(id, PictureOf(id)), null, TimeSpan.Zero, TimeSpan.FromSeconds(1));
            if (_open.TryAdd(id, pictures))
            {
                LockReported?.Invoke(id, _locked.GetValueOrDefault(id));
            }
            else
            {
                pictures.Dispose();
            }
        }

        public void Drop(string deviceId)
        {
            if (_open.TryRemove(deviceId, out var pictures))
            {
                pictures.Dispose();
                Ended?.Invoke(deviceId);
            }
        }

        public void Lock(string deviceId, string message) => Report(deviceId, locked: true);

        public Task UnlockAsync(string deviceId)
        {
            Report(deviceId, locked: false);
            return Task.CompletedTask;
        }

        public void Power(string deviceId, PowerAction action)
        {
            if (!_open.ContainsKey(deviceId) || !on.TryRemove(deviceId, out var pc))
            {
                return;
            }

            PowerAnswered?.Invoke(deviceId, true);
            Drop(deviceId);
            if (action == PowerAction.Restart)
            {
                _ = Task.Delay(RestartTakes).ContinueWith(_ => on[deviceId] = pc, TaskScheduler.Default);
            }
        }

        public Task<IControlSession?> StartControlAsync(string deviceId) => Task.FromResult<IControlSession?>(null);

        private byte[] PictureOf(string deviceId) => receiving.ContainsKey(deviceId) ? broadcastScreen : screens[deviceId];

        private void Report(string deviceId, bool locked)
        {
            if (_open.ContainsKey(deviceId))
            {
                _locked[deviceId] = locked;
                LockReported?.Invoke(deviceId, locked);
            }
        }
    }

    private sealed class DemoBroadcast(ConcurrentDictionary<string, bool> receiving) : IBroadcastController
    {
        public bool HardwareEncoderAvailable => true;

        public PixelSize DisplaySize { get; } = new(1920, 1080);

        public event Action<string>? ClientLost
        {
            add { }
            remove { }
        }

        public event Action? BroadcastFailed
        {
            add { }
            remove { }
        }

        public Task<IReadOnlyCollection<string>> StartAsync(
            IReadOnlyCollection<RoomMember> pcs, QualityPreset preset, Sharpness sharpness, CancellationToken ct)
        {
            foreach (var pc in pcs)
            {
                receiving[pc.DeviceId] = true;
            }

            return Task.FromResult<IReadOnlyCollection<string>>([]);
        }

        public Task<bool> AddAsync(RoomMember pc, CancellationToken ct)
        {
            receiving[pc.DeviceId] = true;
            return Task.FromResult(true);
        }

        public void StopSendingTo(string deviceId) => receiving.TryRemove(deviceId, out _);

        public void Stop() => receiving.Clear();

        public BroadcastStats StatsFor(string deviceId) => new(FramesDropped: 0, FramesOffered: 100);
    }

    private sealed class DemoFiles : IFileSender
    {
        private const int Steps = 25;

        public async Task<SendOutcome?> SendAsync(
            RoomMember pc, SendPackage package, bool open, Action<long> written, CancellationToken ct)
        {
            for (var step = 1; step <= Steps; step++)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(200), ct).ConfigureAwait(false);
                written(package.TotalBytes * step / Steps);
            }

            return SendOutcome.Sent;
        }
    }
}
