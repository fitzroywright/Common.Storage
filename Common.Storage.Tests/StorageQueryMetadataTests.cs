using System.Text;
using Xunit;

namespace Common.Storage.Tests;

public sealed class StorageQueryMetadataTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        "common-storage-query-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ListAsync_FiltersByLogicalPrefixAndHonorsMaximumResults()
    {
        LocalFileStorage storage = new(root);
        await storage.StoreAsync(Request("jobs/1/a.txt", "a"));
        await storage.StoreAsync(Request("jobs/1/b.json", "{\"value\":1}"));
        await storage.StoreAsync(Request("jobs/2/c.txt", "c"));

        IReadOnlyList<StoredFile> values = await storage.ListAsync(
            new StorageListRequest("jobs/1", MaximumResults: 10));

        Assert.Equal(2, values.Count);
        Assert.Equal(
            ["jobs/1/a.txt", "jobs/1/b.json"],
            values.Select(item => item.StorageKey).ToArray());

        IReadOnlyList<StoredFile> bounded = await storage.ListAsync(
            new StorageListRequest("jobs", MaximumResults: 1));
        Assert.Single(bounded);
    }

    [Fact]
    public async Task UpdateMetadataAsync_MergesWithoutRewritingContentOrHistoricalMetadata()
    {
        LocalFileStorage storage = new(root);
        StoredFile original = await storage.StoreAsync(new StorageWriteRequest(
            "media/image.bin",
            new MemoryStream(Encoding.UTF8.GetBytes("payload")),
            "application/octet-stream",
            "image.bin",
            "tester",
            new Dictionary<string, string> { ["Existing"] = "one" }));

        StoredFile updated = await storage.UpdateMetadataAsync(
            original.StorageKey,
            new StorageMetadataUpdate(
                new Dictionary<string, string> { ["Added"] = "two" }));

        Assert.Equal(original.Version, updated.Version);
        Assert.Equal(original.Sha256, updated.Sha256);
        Assert.Equal("one", updated.Metadata["Existing"]);
        Assert.Equal("two", updated.Metadata["Added"]);

        await using Stream content = await storage.OpenReadAsync(original.StorageKey);
        using StreamReader reader = new(content);
        Assert.Equal("payload", await reader.ReadToEndAsync());

        IReadOnlyList<StoredFile> versions = await storage.GetVersionsAsync(original.StorageKey);
        Assert.Single(versions);
        Assert.False(versions[0].Metadata.ContainsKey("Added"));
    }

    [Fact]
    public async Task UpdateMetadataAsync_ReplaceRemovesExistingCustomMetadata()
    {
        LocalFileStorage storage = new(root);
        await storage.StoreAsync(new StorageWriteRequest(
            "replace.bin",
            new MemoryStream([1, 2, 3], writable: false),
            "application/octet-stream",
            "replace.bin",
            "tester",
            new Dictionary<string, string> { ["Old"] = "value" }));

        StoredFile updated = await storage.UpdateMetadataAsync(
            "replace.bin",
            new StorageMetadataUpdate(
                new Dictionary<string, string> { ["New"] = "value" },
                Replace: true));

        Assert.False(updated.Metadata.ContainsKey("Old"));
        Assert.Equal("value", updated.Metadata["New"]);
        Assert.Equal(1, updated.Version);
    }

    [Fact]
    public async Task PrefixedStorage_MapsQueryAndMetadataEditingToLogicalKeys()
    {
        LocalFileStorage inner = new(root);
        PrefixedFileStorage storage = new(inner, "application");
        await storage.StoreAsync(Request("docs/file.txt", "value"));

        IReadOnlyList<StoredFile> listed = await storage.ListAsync(new StorageListRequest("docs"));
        Assert.Single(listed);
        Assert.Equal("docs/file.txt", listed[0].StorageKey);

        StoredFile updated = await storage.UpdateMetadataAsync(
            "docs/file.txt",
            new StorageMetadataUpdate(new Dictionary<string, string> { ["Class"] = "Document" }));

        Assert.Equal("docs/file.txt", updated.StorageKey);
        Assert.Equal("Document", updated.Metadata["Class"]);
        Assert.NotNull(await inner.GetMetadataAsync("application/docs/file.txt"));
    }

    private static StorageWriteRequest Request(string key, string value) =>
        new(
            key,
            new MemoryStream(Encoding.UTF8.GetBytes(value)),
            "application/octet-stream",
            Path.GetFileName(key),
            "tester");

    public void Dispose()
    {
        if (Directory.Exists(root))
            Directory.Delete(root, true);
    }
}
