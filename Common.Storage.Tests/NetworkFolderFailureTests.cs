using Xunit;

namespace Common.Storage.Tests;

public sealed class NetworkFolderFailureTests
{
    [Fact]
    public async Task UnavailableNetworkRoot_ReportsProviderNeutralHealthFailure()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "common-storage-network-unavailable",
            Guid.NewGuid().ToString("N"));

        // Use a path whose parent is removed after construction intent is defined.
        string parent = Path.GetDirectoryName(root)!;
        if (Directory.Exists(parent))
        {
            Directory.Delete(parent, true);
        }

        NetworkFolderStorage storage = new(root);

        StorageHealth health = await storage.CheckHealthAsync();

        Assert.False(health.Available);
        Assert.False(health.Writable);
        Assert.Equal(nameof(NetworkFolderStorage), health.Provider);
        Assert.Equal(root, health.Root);
        Assert.Contains("STORAGE-NETWORK-UNAVAILABLE-001", health.Error);
    }

    [Fact]
    public async Task UnavailableNetworkRoot_StoreThrowsStorageException()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "common-storage-network-unavailable",
            Guid.NewGuid().ToString("N"));

        string parent = Path.GetDirectoryName(root)!;
        if (Directory.Exists(parent))
        {
            Directory.Delete(parent, true);
        }

        NetworkFolderStorage storage = new(root);
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
}
