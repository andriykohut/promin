using Promin.Master.Core.Rooms;
using Promin.Master.Core.Sending;
using Promin.Protocol.Messages;

namespace Promin.Master.Core.Tests.TestDoubles;

/// <summary>
/// A file sender that records every send and leaves it running: the test reports bytes written through
/// <see cref="Send.Written"/> and answers or fails it through <see cref="Send.Answer"/>. A cancelled send ends as
/// cancelled, as the real one does.
/// </summary>
public sealed class FakeFileSender : IFileSender
{
    public List<Send> Sends { get; } = [];

    /// <summary>The newest send to this PC.</summary>
    public Send To(string deviceId) => Sends.Last(s => s.Pc.DeviceId == deviceId);

    public Task<SendOutcome?> SendAsync(RoomMember pc, SendPackage package, bool open, Action<long> written, CancellationToken ct)
    {
        var answer = new TaskCompletionSource<SendOutcome?>();
        ct.Register(() => answer.TrySetCanceled(ct));
        Sends.Add(new Send(pc, package, open, written, answer));
        return answer.Task;
    }

    /// <summary>
    /// A package of files of these sizes. The files are deleted once it is built: a package reads only their sizes until
    /// a real sender opens them, and nothing here does.
    /// </summary>
    public static SendPackage Package(params (string Name, long Bytes)[] files)
    {
        var directory = Directory.CreateTempSubdirectory("promin-tests-");
        try
        {
            var picked = files.Select(f =>
            {
                var path = Path.Combine(directory.FullName, f.Name);
                using (var stream = File.Create(path))
                {
                    stream.SetLength(f.Bytes);
                }

                return new PickedItem(path, IsFolder: false);
            }).ToList();
            return SendPackage.From(picked);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    public sealed record Send(RoomMember Pc, SendPackage Package, bool Open, Action<long> Written, TaskCompletionSource<SendOutcome?> Answer);
}
