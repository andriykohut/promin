using System.Text;
using Educast.Master.Core.Rooms;
using Educast.Master.Core.Setup;
using Educast.Protocol.Transport;

namespace Educast.Master.Core.Tests.Setup;

/// <summary>
/// What the console's pairing adapter is responsible for around the transport: the bytes it
/// sends, how it reads the verdict, and keeping the trust file on disk current.
/// </summary>
public sealed class PairingClientTests : IDisposable
{
    private static readonly StudentPc Pc = new("device-01", "PC-01", "192.168.1.21", 17843);

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "educast-tests", Guid.NewGuid().ToString("N"));
    private readonly string _trustPath;
    private readonly PeerCertificateStore _trust = new();

    public PairingClientTests()
    {
        Directory.CreateDirectory(_directory);
        _trustPath = Path.Combine(_directory, "agents.json");
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private PairingClient Client(PairingFailure answer, List<byte[]>? keys = null) =>
        new(
            (pc, key, ct) =>
            {
                keys?.Add(key);
                return Task.FromResult(answer);
            },
            _trust,
            _trustPath);

    [Fact]
    public async Task PairAsync_SendsTheCode_AsAsciiBytes()
    {
        var keys = new List<byte[]>();

        await Client(PairingFailure.None, keys).PairAsync(Pc, "482913", CancellationToken.None);

        Assert.Equal(Encoding.ASCII.GetBytes("482913"), Assert.Single(keys));
    }

    [Theory]
    [InlineData(PairingFailure.None, PairResult.Paired)]
    [InlineData(PairingFailure.KeyNotProven, PairResult.WrongCode)]
    [InlineData(PairingFailure.AgentUnreachable, PairResult.Unreachable)]
    [InlineData(PairingFailure.RefusedByPeer, PairResult.CannotPairNow)]
    [InlineData(PairingFailure.PeerCertificateRefused, PairResult.CannotPairNow)]
    [InlineData(PairingFailure.TimedOut, PairResult.CannotPairNow)]
    [InlineData(PairingFailure.ConnectionLost, PairResult.CannotPairNow)]
    [InlineData(PairingFailure.NotConfirmed, PairResult.CannotPairNow)]
    [InlineData(PairingFailure.NoMasterArrived, PairResult.CannotPairNow)]
    [InlineData(PairingFailure.WindowSpent, PairResult.CannotPairNow)]
    public async Task PairAsync_MapsEveryVerdict(PairingFailure failure, PairResult expected)
    {
        var result = await Client(failure).PairAsync(Pc, "482913", CancellationToken.None);

        Assert.Equal(expected, result);
    }

    private const string Thumbprint = "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF";

    /// <summary>Gives the store one pin, the way the real one gets them: from the file at start.</summary>
    private void SeedPin(string deviceId)
    {
        File.WriteAllText(_trustPath, $$$"""{"{{{deviceId}}}":"{{{Thumbprint}}}"}""");
        PairingClient.LoadTrust(_trust, _trustPath);
        Assert.True(_trust.TryGetPinnedThumbprint(deviceId, out _));
        File.Delete(_trustPath);
    }

    [Fact]
    public async Task PairAsync_SavesTheTrustFile_OnlyWhenItPaired()
    {
        SeedPin("device-09");

        await Client(PairingFailure.KeyNotProven).PairAsync(Pc, "482913", CancellationToken.None);
        Assert.False(File.Exists(_trustPath));

        await Client(PairingFailure.None).PairAsync(Pc, "482913", CancellationToken.None);
        Assert.Equal($$$"""{"device-09":"{{{Thumbprint}}}"}""", File.ReadAllText(_trustPath));
    }

    [Fact]
    public void IsTrusted_SaysWhetherThisConsoleHoldsAPin()
    {
        SeedPin("device-09");
        var client = Client(PairingFailure.None);

        Assert.True(client.IsTrusted("device-09"));
        Assert.False(client.IsTrusted("device-01"));
    }

    [Fact]
    public async Task PairAsync_RefusesACodeThatIsNotSixDigits()
    {
        var client = Client(PairingFailure.None);

        await Assert.ThrowsAsync<ArgumentException>(() => client.PairAsync(Pc, "482 913", CancellationToken.None));
    }

    [Fact]
    public void Unpair_ForgetsThePin_AndSavesTheTrustFile()
    {
        SeedPin("device-01");

        Client(PairingFailure.None).Unpair("device-01");

        Assert.False(_trust.TryGetPinnedThumbprint("device-01", out _));
        Assert.Equal("{}", File.ReadAllText(_trustPath));
    }

    [Fact]
    public void LoadTrust_WhenTheFileIsMissing_LeavesTheStoreEmpty()
    {
        PairingClient.LoadTrust(_trust, _trustPath);

        Assert.False(File.Exists(_trustPath));
    }

    [Fact]
    public void LoadTrust_WhenTheFileIsCorrupt_SetsItAside()
    {
        File.WriteAllText(_trustPath, "{not json");

        PairingClient.LoadTrust(_trust, _trustPath);

        Assert.False(File.Exists(_trustPath));
        Assert.Equal("{not json", File.ReadAllText(_trustPath + ".corrupt"));
    }
}
