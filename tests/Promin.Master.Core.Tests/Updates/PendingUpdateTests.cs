using Promin.Master.Core.Updates;

namespace Promin.Master.Core.Tests.Updates;

public sealed class PendingUpdateTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("promin-pending-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private string File => Path.Combine(_directory, "Updates", "pending.json");

    [Fact]
    public void RoundTrips()
    {
        new PendingUpdate("0.4.0", ["device-01", "device-02"]).Write(File);

        var read = PendingUpdate.Read(File);
        Assert.Equal("0.4.0", read?.Version);
        Assert.Equal(["device-01", "device-02"], read?.DeviceIds);
    }

    [Fact]
    public void AMissingFile_ReadsNull() => Assert.Null(PendingUpdate.Read(File));

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"version":"0.4.0"}""")]
    [InlineData("""{"deviceIds":[]}""")]
    public void ACorruptFile_ReadsNull(string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(File)!);
        System.IO.File.WriteAllText(File, content);
        Assert.Null(PendingUpdate.Read(File));
    }

    [Fact]
    public void Delete_RemovesIt_AndDoesNotMindNone()
    {
        new PendingUpdate("0.4.0", []).Write(File);
        PendingUpdate.Delete(File);
        Assert.False(System.IO.File.Exists(File));
        PendingUpdate.Delete(File);
    }
}
