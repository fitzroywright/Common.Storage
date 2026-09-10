using System.Text;
using Xunit;

namespace Common.Storage.Tests;

public sealed class StoragePurgeTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "common-storage-purge-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Delete_RemovesCurrentFileButPreservesImmutableHistory()
    {
        LocalFileStorage storage = new(root);
        await storage.StoreAsync(Request("records/document.txt", "version-one"));
        await storage.StoreAsync(Request("records/document.txt", "version-two"));

        await storage.DeleteAsync("records/document.txt");

        Assert.Null(await storage.GetMetadataAsync("records/document.txt"));
        Assert.Equal(2, (await storage.GetVersionsAsync("records/document.txt")).Count);
        await using Stream historical = await storage.OpenVersionAsync("records/document.txt", 1);
        using StreamReader reader = new(historical);
        Assert.Equal("version-one", await reader.ReadToEndAsync());
    }

    [Fact]
    public async Task Purge_RemovesCurrentFileAndAllHistoricalVersions()
    {
        LocalFileStorage storage = new(root);
        await storage.StoreAsync(Request("records/document.txt", "version-one"));
        await storage.StoreAsync(Request("records/document.txt", "version-two"));

        StoragePurgeResult result = await storage.PurgeAsync("records/document.txt");

        Assert.True(result.CurrentFileRemoved);
        Assert.Equal(2, result.VersionsRemoved);
        Assert.True(result.BytesReclaimed > 0);
        Assert.Null(await storage.GetMetadataAsync("records/document.txt"));
        Assert.Empty(await storage.GetVersionsAsync("records/document.txt"));
        await Assert.ThrowsAsync<StorageException>(() => storage.OpenReadAsync("records/document.txt"));
        await Assert.ThrowsAsync<StorageException>(() => storage.OpenVersionAsync("records/document.txt", 1));
    }

    [Fact]
    public async Task PurgeMissingKey_IsIdempotent()
    {
        LocalFileStorage storage = new(root);

        StoragePurgeResult result = await storage.PurgeAsync("missing/document.txt");

        Assert.False(result.CurrentFileRemoved);
        Assert.Equal(0, result.VersionsRemoved);
        Assert.Equal(0, result.BytesReclaimed);
    }

    private static StorageWriteRequest Request(string key, string value)
        => new(
            key,
            new MemoryStream(Encoding.UTF8.GetBytes(value)),
            "text/plain",
            Path.GetFileName(key),
            "tester");

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
