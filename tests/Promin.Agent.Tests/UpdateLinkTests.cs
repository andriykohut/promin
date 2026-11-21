using System.Security.Cryptography;
using System.Threading.Channels;
using Promin.Protocol.Messages;
using Promin.Protocol.Pairing;
using Promin.Protocol.Transport;
using Promin.Protocol.Updates;

namespace Promin.Agent.Tests;

/// <summary>The update stream as the agent handles it, over a fake stream, with setups signed by a test key.</summary>
public sealed class UpdateLinkTests : IDisposable
{
    private readonly TempDirectory _dir = new();
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly CancellationTokenSource _stop = new(TimeSpan.FromSeconds(20));
    private readonly List<string> _log = [];
    private readonly FakeStream _stream = new();
    private int _accepted;

    public void Dispose()
    {
        _stop.Dispose();
        _key.Dispose();
        _dir.Dispose();
    }

    private string UpdateDirectory => Path.Combine(_dir.Path, "update");

    private static ReleaseVersion Parse(string text) =>
        ReleaseVersion.TryParse(text, out var version) ? version : throw new FormatException(text);

    private sealed class FakeStream : IByteChannel
    {
        public Channel<ControlMessage> FromConsole { get; } = Channel.CreateUnbounded<ControlMessage>();
        public Channel<ControlMessage> ToConsole { get; } = Channel.CreateUnbounded<ControlMessage>();

        /// <summary>The raw bytes behind the next offer.</summary>
        public byte[] Bytes { get; set; } = [];

        /// <summary>When set, the stream ends after this many bytes.</summary>
        public int? EndsAfter { get; set; }

        public Task SendAsync(ControlMessage message, CancellationToken ct) => ToConsole.Writer.WriteAsync(message, ct).AsTask();

        public Task<ControlMessage> ReceiveAsync(CancellationToken ct) => FromConsole.Reader.ReadAsync(ct).AsTask();

        public Task SendBytesAsync(ReadOnlyMemory<byte> bytes, CancellationToken ct) => throw new NotSupportedException();

        public async Task ReceiveBytesAsync(Stream destination, long count, CancellationToken ct)
        {
            Assert.Equal(Bytes.Length, count);
            if (EndsAfter is { } end)
            {
                await destination.WriteAsync(Bytes.AsMemory(0, end), ct);
                throw new ControlChannelClosedException("The stream ended.");
            }

            // In two writes, as the network delivers it.
            await destination.WriteAsync(Bytes.AsMemory(0, Bytes.Length / 2), ct);
            await destination.WriteAsync(Bytes.AsMemory(Bytes.Length / 2), ct);
        }
    }

    private sealed class FakeConnection(IByteChannel stream) : IAgentConnection
    {
        public Task<IByteChannel> AcceptUpdateStreamAsync(CancellationToken ct) => Task.FromResult(stream);

        public Task<IControlChannel> AcceptLockStreamAsync(CancellationToken ct) => throw new NotSupportedException();

        public Task<IControlChannel> AcceptRemoteStreamAsync(CancellationToken ct) => throw new NotSupportedException();

        public Task<IIncomingSend> AcceptSendAsync(CancellationToken ct) => throw new NotSupportedException();

        public Task<ControlMessage> ReceiveControlAsync(CancellationToken ct) => throw new NotSupportedException();

        public ValueTask<byte[]> ReceiveVideoFrameAsync(CancellationToken ct) => throw new NotSupportedException();

        public Task SendControlAsync(ControlMessage message, CancellationToken ct) => throw new NotSupportedException();
    }

    private Task RunAsync(string installed = "0.3.0") =>
        new UpdateLink(Parse(installed), UpdateDirectory, _key.ExportSubjectPublicKeyInfo(), () => _accepted++, _log.Add)
            .RunAsync(new FakeConnection(_stream), _stop.Token);

    private OfferUpdate Offer(string version, byte[] bytes, string? signedAs = null, string signedFor = UpdateLink.SetupName)
    {
        _stream.Bytes = bytes;
        var signature = _key.SignData(
            UpdateSignature.SignedBytes(signedFor, Parse(signedAs ?? version), SHA256.HashData(bytes)), HashAlgorithmName.SHA256);
        return new OfferUpdate(version, bytes.Length, signature);
    }

    private async Task<ControlMessage> NextAsync() => await _stream.ToConsole.Reader.ReadAsync(_stop.Token);

    /// <summary>Reads the version the link opens with, sends <paramref name="offer"/> and returns the answer.</summary>
    private async Task<UpdateOutcome> AnswerToAsync(OfferUpdate offer)
    {
        await NextAsync();
        await _stream.FromConsole.Writer.WriteAsync(offer, _stop.Token);
        return Assert.IsType<UpdateAnswer>(await NextAsync()).Outcome;
    }

    private string[] Kept() => Directory.Exists(UpdateDirectory)
        ? Directory.GetFileSystemEntries(UpdateDirectory).Select(e => Path.GetFileName(e)!).ToArray()
        : [];

    [Fact]
    public async Task AnswersTheVersionFirst()
    {
        var running = RunAsync();
        Assert.Equal(new AgentVersion("0.3.0"), await NextAsync());
        await _stop.CancelAsync();
        await running;
    }

    [Fact]
    public async Task AcceptsANewerSignedSetup()
    {
        var bytes = RandomNumberGenerator.GetBytes(10_000);
        var running = RunAsync();

        Assert.Equal(UpdateOutcome.Accepted, await AnswerToAsync(Offer("0.4.0", bytes)));
        await running;

        Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(UpdateDirectory, UpdateLink.SetupName)));
        var sig = SetupSignature.Read(Path.Combine(UpdateDirectory, UpdateLink.SetupName + SetupSignature.Extension));
        Assert.Equal("0.4.0", sig?.Version);
        Assert.Equal(2, Kept().Length);
        Assert.Equal(1, _accepted);
    }

    [Fact]
    public async Task RefusesABadSignature()
    {
        var running = RunAsync();
        Assert.Equal(UpdateOutcome.BadSignature, await AnswerToAsync(Offer("0.4.0", [1, 2, 3], signedAs: "0.5.0")));
        Assert.Empty(Kept());
        Assert.Equal(0, _accepted);
        await _stop.CancelAsync();
        await running;
    }

    [Fact]
    public async Task RefusesTheConsoleSetup()
    {
        var running = RunAsync();
        var offer = Offer("0.4.0", [1, 2, 3], signedFor: "Promin-Console-Setup.exe");
        Assert.Equal(UpdateOutcome.BadSignature, await AnswerToAsync(offer));
        Assert.Empty(Kept());
        Assert.Equal(0, _accepted);
        await _stop.CancelAsync();
        await running;
    }

    [Fact]
    public async Task RefusesANotNewerVersion()
    {
        var running = RunAsync(installed: "0.4.0");
        Assert.Equal(UpdateOutcome.NotNewer, await AnswerToAsync(Offer("0.4.0", [1, 2, 3])));
        Assert.Empty(Kept());
        Assert.Equal(0, _accepted);
        await _stop.CancelAsync();
        await running;
    }

    [Fact]
    public async Task RefusesAnUnreadableVersion()
    {
        var running = RunAsync();
        var offer = Offer("0.4.0", [1, 2, 3]) with { Version = "garbage" };
        Assert.Equal(UpdateOutcome.BadSignature, await AnswerToAsync(offer));
        Assert.Empty(Kept());
        await _stop.CancelAsync();
        await running;
    }

    [Fact]
    public async Task TakesAnotherOfferAfterARefusal()
    {
        var running = RunAsync();
        Assert.Equal(UpdateOutcome.BadSignature, await AnswerToAsync(Offer("0.4.0", [1, 2, 3], signedAs: "0.5.0")));

        await _stream.FromConsole.Writer.WriteAsync(Offer("0.4.0", [4, 5, 6]), _stop.Token);
        Assert.Equal(new UpdateAnswer(UpdateOutcome.Accepted), await NextAsync());
        await running;
        Assert.Equal(1, _accepted);
    }

    [Fact]
    public async Task KeepsNothingWhenTheStreamEndsEarly()
    {
        var running = RunAsync();
        await NextAsync();
        _stream.EndsAfter = 5;
        await _stream.FromConsole.Writer.WriteAsync(Offer("0.4.0", RandomNumberGenerator.GetBytes(10)), _stop.Token);

        await running;
        Assert.False(_stream.ToConsole.Reader.TryRead(out _));
        Assert.Empty(Kept());
        Assert.Equal(0, _accepted);
    }

    [Fact]
    public async Task AnswersFailedWhenTheSetupCannotBeSaved()
    {
        // A folder where the setup's file goes: the file cannot be created.
        Directory.CreateDirectory(Path.Combine(UpdateDirectory, UpdateLink.SetupName + ".part"));
        var running = RunAsync();

        Assert.Equal(UpdateOutcome.Failed, await AnswerToAsync(Offer("0.4.0", [1, 2, 3])));
        Assert.DoesNotContain(UpdateLink.SetupName, Kept());
        Assert.DoesNotContain(UpdateLink.SetupName + SetupSignature.Extension, Kept());
        Assert.Equal(0, _accepted);
        await _stop.CancelAsync();
        await running;
    }

    [Fact]
    public void ClearLeftoversEmptiesTheFolder()
    {
        Directory.CreateDirectory(UpdateDirectory);
        File.WriteAllBytes(Path.Combine(UpdateDirectory, UpdateLink.SetupName), [1]);
        File.WriteAllBytes(Path.Combine(UpdateDirectory, UpdateLink.SetupName + SetupSignature.Extension), [1]);

        UpdateLink.ClearLeftovers(UpdateDirectory, _log.Add);
        Assert.Empty(Kept());
    }

    [Fact]
    public void ClearLeftoversIgnoresAFileInUse()
    {
        Directory.CreateDirectory(UpdateDirectory);
        var setup = Path.Combine(UpdateDirectory, UpdateLink.SetupName);
        using var held = new FileStream(setup, FileMode.Create, FileAccess.Write, FileShare.None);

        UpdateLink.ClearLeftovers(UpdateDirectory, _log.Add);
        Assert.True(File.Exists(setup));
    }

    [Fact]
    public void ClearLeftoversWithNoFolderDoesNothing() => UpdateLink.ClearLeftovers(UpdateDirectory, _log.Add);
}
