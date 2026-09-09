using System.Text;
using Xunit;

namespace Common.Storage.Tests;

public sealed class LocalFileStorageTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "common-storage-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task StoreAndRead_VerifiesContentAndMetadata()
    {
        LocalFileStorage storage = new(root);
        await using MemoryStream content = new(Encoding.UTF8.GetBytes("hello"));
        StoredFile stored = await storage.StoreAsync(new StorageWriteRequest("jobs/1/file.txt", content, "text/plain", "file.txt", "tester"));

        Assert.Equal(1, stored.Version);
        Assert.Equal(5, stored.Length);
        Assert.Equal(64, stored.Sha256.Length);
        await using Stream read = await storage.OpenReadAsync(stored.StorageKey);
        using StreamReader reader = new(read);
        Assert.Equal("hello", await reader.ReadToEndAsync());
    }

    [Fact]
    public async Task ReplacingAKey_CreatesImmutableVersions()
    {
        LocalFileStorage storage = new(root);
        await storage.StoreAsync(Request("same.bin", "one"));
        StoredFile second = await storage.StoreAsync(Request("same.bin", "two"));
        IReadOnlyList<StoredFile> versions = await storage.GetVersionsAsync("same.bin");

        Assert.Equal(2, second.Version);
        Assert.Equal(2, versions.Count);
        Assert.Equal([2, 1], versions.Select(item => item.Version).ToArray());
    }

    [Fact]
    public async Task OversizeUpload_FailsWithoutPublishingCurrentFile()
    {
        LocalFileStorage storage = new(root);
        await using MemoryStream content = new(new byte[10]);
        await Assert.ThrowsAsync<StorageException>(() => storage.StoreAsync(new StorageWriteRequest("large.bin", content, "application/octet-stream", "large.bin", "tester", MaximumBytes: 5)));
        Assert.Null(await storage.GetMetadataAsync("large.bin"));
    }

    [Fact]
    public async Task PathTraversal_IsRejected()
    {
        LocalFileStorage storage = new(root);
        await Assert.ThrowsAsync<StorageException>(() => storage.StoreAsync(Request("../escape.txt", "bad")));
    }

    [Fact]
    public async Task Corruption_IsDetectedOnRead()
    {
        LocalFileStorage storage = new(root);
        await storage.StoreAsync(Request("integrity.bin", "good"));
        await File.WriteAllTextAsync(Path.Combine(root, "integrity.bin"), "tampered");
        await Assert.ThrowsAsync<StorageException>(() => storage.OpenReadAsync("integrity.bin"));
    }

    [Fact]
    public async Task HealthProbe_ReportsWritableStorage()
    {
        LocalFileStorage storage = new(root);
        StorageHealth health = await storage.CheckHealthAsync();
        Assert.True(health.Available);
        Assert.True(health.Writable);
    }

    private static StorageWriteRequest Request(string key, string value)
        => new(key, new MemoryStream(Encoding.UTF8.GetBytes(value)), "application/octet-stream", Path.GetFileName(key), "tester");

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
