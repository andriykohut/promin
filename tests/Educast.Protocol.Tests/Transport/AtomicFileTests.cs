using Educast.Protocol.Transport;

namespace Educast.Protocol.Tests.Transport;

public sealed class AtomicFileTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "educast-atomic-file-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void WriteAllBytes_CreatesTheDirectoryAndTheFile_AndLeavesNothingElse()
    {
        var path = Path.Combine(_root, "a", "b", "file.bin");

        AtomicFile.WriteAllBytes(path, [1, 2, 3]);

        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(path));
        Assert.Equal([path], Directory.GetFiles(Path.GetDirectoryName(path)!));
    }

    [Fact]
    public void WriteAllBytes_ReplacesAnExistingFileWholesale()
    {
        var path = Path.Combine(_root, "file.bin");
        AtomicFile.WriteAllBytes(path, [1, 2, 3, 4, 5, 6]);

        AtomicFile.WriteAllBytes(path, [9]);

        Assert.Equal(new byte[] { 9 }, File.ReadAllBytes(path));
        Assert.Single(Directory.GetFiles(_root));
    }

    [Fact]
    public void WriteAllBytes_WhenTheSwapFails_LeavesNoTemporaryFileBehind()
    {
        // A directory where the file should go: the swap cannot succeed.
        var path = Path.Combine(_root, "file.bin");
        Directory.CreateDirectory(path);

        Assert.ThrowsAny<IOException>(() => AtomicFile.WriteAllBytes(path, [1]));

        Assert.Empty(Directory.GetFiles(_root));
        Assert.True(Directory.Exists(path));
    }

    [Fact]
    public void WriteAllBytes_WhenTheDestinationIsLocked_KeepsTheOldContentAndLeavesNoTemporaryFile()
    {
        var path = Path.Combine(_root, "file.bin");
        AtomicFile.WriteAllBytes(path, [1, 2, 3]);

        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.ThrowsAny<IOException>(() => AtomicFile.WriteAllBytes(path, [9, 9]));
        }

        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(path));
        Assert.Single(Directory.GetFiles(_root));
    }
}
