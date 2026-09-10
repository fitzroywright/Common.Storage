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
    public async Task ConcurrentWritesToSameKey_AreSerializedIntoDistinctVersions()
    {
        LocalFileStorage storage = new(root);
        Task<StoredFile>[] writes = Enumerable.Range(1, 8)
            .Select(index => storage.StoreAsync(Request("concurrent.bin", $"value-{index}")))
            .ToArray();

        StoredFile[] stored = await Task.WhenAll(writes);
        Assert.Equal(Enumerable.Range(1, 8), stored.Select(item => item.Version).Order());
        Assert.Equal(8, (await storage.GetVersionsAsync("concurrent.bin")).Count);
    }

    [Fact]
    public async Task ConcurrentWritesAcrossProviderInstances_AreSerializedIntoDistinctVersions()
    {
        LocalFileStorage storageA = new(root);
        LocalFileStorage storageB = new(root);
        Task<StoredFile>[] writes = Enumerable.Range(1, 12)
            .Select(index => (index % 2 == 0 ? storageA : storageB)
                .StoreAsync(Request("shared/concurrent.bin", $"value-{index}")))
            .ToArray();

        StoredFile[] stored = await Task.WhenAll(writes);

        Assert.Equal(Enumerable.Range(1, 12), stored.Select(item => item.Version).Order());
        Assert.Equal(12, (await storageA.GetVersionsAsync("shared/concurrent.bin")).Count);
    }

    [Fact]
    public async Task DeleteAcrossProviderInstances_WaitsForActiveWriter()
    {
        LocalFileStorage writer = new(root);
        LocalFileStorage deleter = new(root);
        await using BlockingReadStream source = new(Encoding.UTF8.GetBytes("serialized-write"));

        Task<StoredFile> write = writer.StoreAsync(new StorageWriteRequest(
            "shared/delete-race.bin",
            source,
            "application/octet-stream",
            "delete-race.bin",
            "writer"));

        await source.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task delete = deleter.DeleteAsync("shared/delete-race.bin");
        await Task.Delay(100);
        Assert.False(delete.IsCompleted);

        source.Release();
        StoredFile stored = await write;
        await delete;

        Assert.Equal(1, stored.Version);
        Assert.Null(await writer.GetMetadataAsync("shared/delete-race.bin"));
        await Assert.ThrowsAsync<StorageException>(() => writer.OpenReadAsync("shared/delete-race.bin"));
        Assert.Single(await writer.GetVersionsAsync("shared/delete-race.bin"));
    }

    [Fact]
    public async Task OpenVersionAndRestoreVersion_PreserveHistoryAndIntegrity()
    {
        LocalFileStorage storage = new(root);
        await storage.StoreAsync(Request("restore.bin", "original"));
        await storage.StoreAsync(Request("restore.bin", "replacement"));

        await using (Stream version = await storage.OpenVersionAsync("restore.bin", 1))
        using (StreamReader reader = new(version))
            Assert.Equal("original", await reader.ReadToEndAsync());

        StoredFile restored = await storage.RestoreVersionAsync("restore.bin", 1, "operator");
        Assert.Equal(3, restored.Version);
        Assert.Equal("1", restored.Metadata["RestoredFromVersion"]);
        await using Stream current = await storage.OpenReadAsync("restore.bin");
        using StreamReader currentReader = new(current);
        Assert.Equal("original", await currentReader.ReadToEndAsync());
    }

    [Fact]
    public async Task Maintenance_RemovesStaleTemporaryFilesAndOldVersions()
    {
        LocalFileStorage storage = new(root);
        await storage.StoreAsync(Request("retained.bin", "one"));
        await storage.StoreAsync(Request("retained.bin", "two"));
        await storage.StoreAsync(Request("retained.bin", "three"));
        string stale = Path.Combine(root, "orphan.uploading");
        await File.WriteAllTextAsync(stale, "orphan");
        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddDays(-2));

        StorageMaintenanceResult result = await storage.RunMaintenanceAsync(new StorageMaintenanceOptions(TimeSpan.FromHours(1), 2));

        Assert.Equal(1, result.TemporaryFilesRemoved);
        Assert.Equal(1, result.VersionsRemoved);
        Assert.False(File.Exists(stale));
        Assert.Equal(2, (await storage.GetVersionsAsync("retained.bin")).Count);
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
    public async Task LargeUpload_IsStreamedInBoundedChunks()
    {
        LocalFileStorage storage = new(root);
        await using TrackingMemoryStream content = new(new byte[2 * 1024 * 1024]);

        StoredFile stored = await storage.StoreAsync(new StorageWriteRequest(
            "streaming/large.bin",
            content,
            "application/octet-stream",
            "large.bin",
            "tester",
            MaximumBytes: 3 * 1024 * 1024));

        Assert.Equal(2 * 1024 * 1024, stored.Length);
        Assert.InRange(content.MaximumRequestedRead, 1, 128 * 1024);
    }

    [Fact]
    public async Task PathTraversal_IsRejected()
    {
        LocalFileStorage storage = new(root);
        await Assert.ThrowsAsync<StorageException>(() => storage.StoreAsync(Request("../escape.txt", "bad")));
    }

    [Fact]
    public async Task UnsafeCrossPlatformPathCharacters_AreRejected()
    {
        LocalFileStorage storage = new(root);
        StorageException exception = await Assert.ThrowsAsync<StorageException>(() => storage.StoreAsync(Request("safe/file.txt:alternate", "bad")));
        Assert.Equal("STORAGE-PATH-005", exception.Code);
    }

    [Fact]
    public async Task SymlinkEscape_IsRejectedOnUnix()
    {
        if (OperatingSystem.IsWindows()) return;

        Directory.CreateDirectory(root);
        string outside = Path.Combine(Path.GetTempPath(), "common-storage-outside", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        string link = Path.Combine(root, "linked");
        Directory.CreateSymbolicLink(link, outside);
        try
        {
            LocalFileStorage storage = new(root);
            StorageException exception = await Assert.ThrowsAsync<StorageException>(() => storage.StoreAsync(Request("linked/escape.txt", "bad")));
            Assert.Equal("STORAGE-PATH-004", exception.Code);
        }
        finally
        {
            if (Directory.Exists(link)) Directory.Delete(link);
            if (Directory.Exists(outside)) Directory.Delete(outside, true);
        }
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
    public async Task CorruptHistoricalVersion_IsDetectedOnRead()
    {
        LocalFileStorage storage = new(root);
        await storage.StoreAsync(Request("history.bin", "good"));
        await File.WriteAllTextAsync(Path.Combine(root, "history.bin.versions", "00000001.bin"), "tampered");
        await Assert.ThrowsAsync<StorageException>(() => storage.OpenVersionAsync("history.bin", 1));
    }

    [Fact]
    public async Task HealthProbe_ReportsWritableStorage()
    {
        LocalFileStorage storage = new(root);
        StorageHealth health = await storage.CheckHealthAsync();
        Assert.True(health.Available);
        Assert.True(health.Writable);
    }

    [Fact]
    public async Task HealthProbe_ReportsUnavailableWhenStorageRootDisappears()
    {
        LocalFileStorage storage = new(root);
        Directory.Delete(root, true);

        StorageHealth health = await storage.CheckHealthAsync();

        Assert.False(health.Available);
        Assert.False(health.Writable);
        Assert.False(string.IsNullOrWhiteSpace(health.Error));
    }

    private static StorageWriteRequest Request(string key, string value)
        => new(key, new MemoryStream(Encoding.UTF8.GetBytes(value)), "application/octet-stream", Path.GetFileName(key), "tester");

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }

    private sealed class TrackingMemoryStream(byte[] buffer) : MemoryStream(buffer, writable: false)
    {
        public int MaximumRequestedRead { get; private set; }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            MaximumRequestedRead = Math.Max(MaximumRequestedRead, buffer.Length);
            return base.ReadAsync(buffer, cancellationToken);
        }
    }

    private sealed class BlockingReadStream(byte[] buffer) : MemoryStream(buffer, writable: false)
    {
        private readonly TaskCompletionSource readStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool blocked;

        public TaskCompletionSource ReadStarted => readStarted;

        public void Release() => released.TrySetResult();

        public override async ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken cancellationToken = default)
        {
            if (!blocked)
            {
                blocked = true;
                readStarted.TrySetResult();
                await released.Task.WaitAsync(cancellationToken);
            }
            return await base.ReadAsync(destination, cancellationToken);
        }
    }
}
