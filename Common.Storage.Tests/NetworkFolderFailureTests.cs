using Xunit;

namespace Common.Storage.Tests;

public sealed class NetworkFolderFailureTests : IDisposable
{
    private readonly string tempRoot = Path.Combine(
        Path.GetTempPath(),
        "common-storage-network-failure-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task UnavailableNetworkRoot_ReportsProviderNeutralHealthFailure()
    {
        string blockingFile = CreateBlockingFile();
        string unavailableRoot = Path.Combine(blockingFile, "share");

        NetworkFolderStorage storage = new(unavailableRoot);

        StorageHealth health = await storage.CheckHealthAsync();

        Assert.False(health.Available);
        Assert.False(health.Writable);
        Assert.Equal(nameof(NetworkFolderStorage), health.Provider);
        Assert.Equal(unavailableRoot, health.Root);
        Assert.Contains("STORAGE-NETWORK-UNAVAILABLE-001", health.Error);
    }

    [Fact]
    public async Task UnavailableNetworkRoot_StoreThrowsStorageException()
    {
        string blockingFile = CreateBlockingFile();
        string unavailableRoot = Path.Combine(blockingFile, "share");

        NetworkFolderStorage storage = new(unavailableRoot);
        await using MemoryStream content = new([1, 2, 3], writable: false);

        StorageException exception = await Assert.ThrowsAsync<StorageException>(() =>
            storage.StoreAsync(new StorageWriteRequest(
                "proof/test.bin",
                content,
                "application/octet-stream",
                "test.bin",
                "NetworkFolderFailureTests")));

        Assert.Equal("STORAGE-NETWORK-UNAVAILABLE-001", exception.Code);
    }

    private string CreateBlockingFile()
    {
        Directory.CreateDirectory(tempRoot);
        string path = Path.Combine(tempRoot, "not-a-directory");
        File.WriteAllText(path, "blocking file");
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(tempRoot))
        {
            Directory.Delete(tempRoot, true);
        }
    }
}
