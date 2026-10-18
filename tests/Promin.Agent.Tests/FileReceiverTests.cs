using System.ComponentModel;
using System.Threading.Channels;
using Promin.Protocol.Messages;
using Promin.Protocol.Sending;

namespace Promin.Agent.Tests;

/// <summary>
/// Sends on one console PC's connection: each is received as the signed-in person, one at a time, and one that
/// arrives whole starts the helper. The person's folders are under a temp directory, and the helper is never started.
/// </summary>
public class FileReceiverTests
{
    private sealed class FakeConnection : IAgentConnection
    {
        private readonly Channel<IIncomingSend> _sends = Channel.CreateUnbounded<IIncomingSend>();

        public Task<IIncomingSend> AcceptSendAsync(CancellationToken ct) => _sends.Reader.ReadAsync(ct).AsTask();

        public Task<ControlMessage> ReceiveControlAsync(CancellationToken ct) => throw new NotSupportedException();

        public ValueTask<byte[]> ReceiveVideoFrameAsync(CancellationToken ct) => throw new NotSupportedException();

        public Task SendControlAsync(ControlMessage message, CancellationToken ct) => throw new NotSupportedException();

        public void Offer(IIncomingSend send) => _sends.Writer.TryWrite(send);

        public void Lose() => _sends.Writer.TryComplete(new IOException("the connection was lost"));
    }

    /// <summary>Anna, with her folders under <paramref name="home"/>. <see cref="Acting"/> is true only inside <see cref="RunAsAsync{T}"/>.</summary>
    private sealed class FakePerson(string home) : ISignedInPerson
    {
        private readonly AsyncLocal<bool> _acting = new();
        private readonly List<IReadOnlyList<string>> _helpers = [];

        public Func<PersonFolders?> OnFolders { get; set; } =
            () => new PersonFolders(Path.Combine(home, "Desktop"), Path.Combine(home, "LocalAppData"));

        public Exception? HelperFailure { get; set; }

        public bool Acting => _acting.Value;

        public IReadOnlyList<string>[] Helpers
        {
            get
            {
                lock (_helpers)
                {
                    return [.. _helpers];
                }
            }
        }

        public string? Name => @"PC\anna";

        public PersonFolders? Folders() => OnFolders();

        public async Task<T> RunAsAsync<T>(Func<Task<T>> work)
        {
            _acting.Value = true;
            return await work();
        }

        public Task<int?> RunHelperAsync(IReadOnlyList<string> arguments, TimeSpan wait)
        {
            lock (_helpers)
            {
                _helpers.Add(arguments);
            }

            return HelperFailure is { } failure ? Task.FromException<int?>(failure) : Task.FromResult<int?>(null);
        }
    }

    /// <summary>
    /// A send of <paramref name="items"/> (a name ending in '/' is a folder) that drives the target as the exchange
    /// does: Begin, the items, Finish. <see cref="Done"/> completes when the receiver lets it go.
    /// </summary>
    private sealed class FakeSend(FakePerson person, bool open, params string[] items) : IIncomingSend
    {
        private readonly TaskCompletionSource _go = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _held;

        public SendFiles Header { get; } = new(0, open);

        /// <summary>The exchange answers this without driving the target, as it does for a send it refused.</summary>
        public SendOutcome? Answer { get; set; }

        /// <summary>The target fails with this while writing, and the exchange answers Failed.</summary>
        public IOException? WriteFails { get; set; }

        /// <summary>The stream ends early with this.</summary>
        public Exception? EndsEarly { get; set; }

        /// <summary>Whether the target was driven inside the person's identity; null when it was never driven.</summary>
        public bool? DrivenAsPerson { get; private set; }

        public SendOutcome? Refused { get; private set; }

        public Task Done => _done.Task;

        /// <summary>Keeps <see cref="ReceiveAsync"/> waiting until <see cref="LetGo"/>.</summary>
        public FakeSend Hold()
        {
            _held = true;
            return this;
        }

        public void LetGo() => _go.TrySetResult();

        public async Task<SendOutcome> ReceiveAsync(IReceiveTarget target, CancellationToken ct, Action<Exception>? failed = null)
        {
            if (_held)
            {
                await _go.Task.WaitAsync(ct);
            }

            DrivenAsPerson = person.Acting;
            if (EndsEarly is { } ended)
            {
                target.Discard();
                throw ended;
            }

            if (Answer is { } answer)
            {
                return answer;
            }

            target.Begin(Header.TotalBytes);
            if (WriteFails is { } failure)
            {
                failed?.Invoke(failure);
                target.Discard();
                return SendOutcome.Failed;
            }

            foreach (var item in items)
            {
                if (item.EndsWith('/'))
                {
                    target.AddFolder(item.TrimEnd('/'));
                }
                else
                {
                    target.AddFile(item, 0).Dispose();
                }
            }

            return target.Finish();
        }

        public Task RefuseAsync(SendOutcome outcome, CancellationToken ct)
        {
            Refused = outcome;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            _done.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class Rig : IAsyncDisposable
    {
        private readonly TempDirectory _home = new();
        private readonly CancellationTokenSource _stop = new(TimeSpan.FromSeconds(20));
        private readonly List<string> _log = [];
        private int _linesLoggedAsPerson;

        public Rig()
        {
            Person = new FakePerson(_home.Path);
            Running = new FileReceiver(
                () => Person,
                line =>
                {
                    lock (_log)
                    {
                        _log.Add(line);
                        _linesLoggedAsPerson += Person.Acting ? 1 : 0;
                    }
                }).RunAsync(Connection, _stop.Token);
        }

        public FakeConnection Connection { get; } = new();
        public FakePerson Person { get; }
        public Task Running { get; }
        public string Promin => Path.Combine(_home.Path, "Desktop", "Promin");

        public string[] Log
        {
            get
            {
                lock (_log)
                {
                    return [.. _log];
                }
            }
        }

        /// <summary>Lines written inside the person's identity, where the agent's log, locked to SYSTEM, cannot be written.</summary>
        public int LinesLoggedAsPerson
        {
            get
            {
                lock (_log)
                {
                    return _linesLoggedAsPerson;
                }
            }
        }

        public FakeSend Send(bool open, params string[] items) => new(Person, open, items);

        /// <summary>Offers <paramref name="send"/> and waits until the receiver has let it go.</summary>
        public async Task ReceiveAsync(FakeSend send)
        {
            Connection.Offer(send);
            await send.Done.WaitAsync(TimeSpan.FromSeconds(10));
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            await Running.WaitAsync(TimeSpan.FromSeconds(5));
            _stop.Dispose();
            _home.Dispose();
        }
    }

    [Fact]
    public async Task ASentSend_StartsTheHelper_WithTheCountFolderAndWhatToOpen()
    {
        await using var rig = new Rig();

        await rig.ReceiveAsync(rig.Send(open: true, "quiz.html", "quiz_files/"));

        Assert.Equal(["--received", "2", rig.Promin, rig.Promin + @"\quiz.html"], Assert.Single(rig.Person.Helpers));
    }

    [Fact]
    public async Task ASendNotToBeOpened_StartsTheHelperForTheNotice()
    {
        await using var rig = new Rig();

        await rig.ReceiveAsync(rig.Send(open: false, "notes.txt"));

        Assert.Equal(["--received", "1", rig.Promin], Assert.Single(rig.Person.Helpers));
    }

    [Fact]
    public async Task TheFileWorkRunsAsThePerson()
    {
        await using var rig = new Rig();
        var send = rig.Send(open: false, "notes.txt");

        await rig.ReceiveAsync(send);

        Assert.True(send.DrivenAsPerson);
    }

    [Fact]
    public async Task ASecondSendWhileOneRuns_IsRefusedAsBusy()
    {
        await using var rig = new Rig();
        var first = rig.Send(open: false, "a.txt").Hold();
        rig.Connection.Offer(first);

        var second = rig.Send(open: false, "b.txt");
        await rig.ReceiveAsync(second);

        Assert.Equal(SendOutcome.Busy, second.Refused);
        Assert.Null(second.DrivenAsPerson);
        Assert.Contains(rig.Log, line => line.Contains("Busy"));

        first.LetGo();
        await first.Done.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Null(first.Refused);
        Assert.Single(rig.Person.Helpers);
    }

    [Fact]
    public async Task NobodySignedIn_IsRefusedAsFailed()
    {
        await using var rig = new Rig();
        rig.Person.OnFolders = () => null;
        var send = rig.Send(open: true, "a.txt");

        await rig.ReceiveAsync(send);

        Assert.Equal(SendOutcome.Failed, send.Refused);
        Assert.Null(send.DrivenAsPerson);
        Assert.Contains(rig.Log, line => line.StartsWith("Nobody is signed in"));
        Assert.Empty(rig.Person.Helpers);
    }

    [Fact]
    public async Task FoldersWindowsCannotGive_AreRefusedAsFailed()
    {
        await using var rig = new Rig();
        rig.Person.OnFolders = () => throw new Win32Exception(1314, "A required privilege is not held by the client.");
        var send = rig.Send(open: true, "a.txt");

        await rig.ReceiveAsync(send);

        Assert.Equal(SendOutcome.Failed, send.Refused);
        Assert.Contains(rig.Log, line => line.Contains("A required privilege is not held by the client."));
        Assert.Empty(rig.Person.Helpers);
    }

    [Theory]
    [InlineData(SendOutcome.NotEnoughSpace)]
    [InlineData(SendOutcome.BadPath)]
    [InlineData(SendOutcome.InUse)]
    [InlineData(SendOutcome.Failed)]
    public async Task ARefusedOrFailedSend_StartsNoHelper(SendOutcome answer)
    {
        await using var rig = new Rig();
        var send = rig.Send(open: true, "a.txt");
        send.Answer = answer;

        await rig.ReceiveAsync(send);

        Assert.Empty(rig.Person.Helpers);
        Assert.Contains(rig.Log, line => line.Contains(answer.ToString()));
    }

    [Fact]
    public async Task ASendThatEndsEarly_IsLogged_AndStartsNoHelper()
    {
        await using var rig = new Rig();
        var send = rig.Send(open: true, "a.txt");
        send.EndsEarly = new IOException("The console PC stopped the send.");

        await rig.ReceiveAsync(send);

        Assert.Empty(rig.Person.Helpers);
        Assert.Contains(rig.Log, line => line.Contains("The console PC stopped the send."));
    }

    [Fact]
    public async Task WhyAWriteFailed_IsLogged_AsTheAgent()
    {
        await using var rig = new Rig();
        var send = rig.Send(open: false, "a.txt");
        send.WriteFails = new IOException("The disk is full.");

        await rig.ReceiveAsync(send);

        Assert.Contains(rig.Log, line => line.Contains("The disk is full."));
        Assert.Equal(0, rig.LinesLoggedAsPerson);
    }

    [Fact]
    public async Task WhyItemsWereNotPutInPlace_IsLogged()
    {
        await using var rig = new Rig();
        Directory.CreateDirectory(Path.GetDirectoryName(rig.Promin)!);
        File.WriteAllText(rig.Promin, "a file where the Promin folder goes");

        await rig.ReceiveAsync(rig.Send(open: false, "a.txt"));

        Assert.Contains(rig.Log, line => line.Contains(nameof(SendOutcome.Failed)) && line.Contains(rig.Promin));
    }

    [Fact]
    public async Task AHelperThatCannotStart_IsLogged_AndTheNextSendIsStillTaken()
    {
        await using var rig = new Rig();
        rig.Person.HelperFailure = new Win32Exception(2, "The system cannot find the file specified.");

        await rig.ReceiveAsync(rig.Send(open: false, "a.txt"));
        var next = rig.Send(open: false, "b.txt");
        await rig.ReceiveAsync(next);

        Assert.Contains(rig.Log, line => line.Contains("The system cannot find the file specified."));
        Assert.Null(next.Refused);
        Assert.Equal(2, rig.Person.Helpers.Length);
    }

    [Fact]
    public async Task TheReceiverEnds_WhenTheConnectionDoes()
    {
        await using var rig = new Rig();

        rig.Connection.Lose();

        await rig.Running.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(rig.Running.IsCompletedSuccessfully);
    }

    /// <summary>Staging is one folder per person, so the next connection's receiver must not start while a send still runs.</summary>
    [Fact]
    public async Task WhenTheConnectionEnds_TheRunningSendIsWaitedFor()
    {
        await using var rig = new Rig();
        var send = rig.Send(open: false, "a.txt").Hold();
        rig.Connection.Offer(send);

        rig.Connection.Lose();
        await Task.Delay(100);
        Assert.False(rig.Running.IsCompleted);

        send.LetGo();
        await rig.Running.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(send.Done.IsCompleted);
    }
}
