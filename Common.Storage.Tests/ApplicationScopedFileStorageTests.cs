namespace Common.Storage.Tests;

public sealed class ApplicationScopedFileStorageTests
{
    [Fact]
    public async Task Scoped_storage_adds_application_owner_and_allows_owner_read()
    {
        string root = Path.Combine(Path.GetTempPath(), "common-storage-owner-" + Guid.NewGuid().ToString("N"));
        try
        {
            var raw = new LocalFileStorage(root);
            var scoped = new ApplicationScopedFileStorage(raw, "Aegis.Studio");
            await using var source = new MemoryStream("hello"u8.ToArray());

            StoredFile stored = await scoped.StoreAsync(
                new StorageWriteRequest("attachments/a.txt", source, "text/plain", "a.txt", "user"));

            Assert.Equal("Aegis.Studio", stored.Metadata[ApplicationScopedFileStorage.ApplicationMetadataKey]);
            await using Stream opened = await scoped.OpenReadAsync("attachments/a.txt");
            Assert.True(opened.CanRead);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Different_application_scope_cannot_read_owned_object()
    {
        string root = Path.Combine(Path.GetTempPath(), "common-storage-owner-" + Guid.NewGuid().ToString("N"));
        try
        {
            var raw = new LocalFileStorage(root);
            var studio = new ApplicationScopedFileStorage(raw, "Aegis.Studio");
            var portal = new ApplicationScopedFileStorage(raw, "RequestPortal");
            await using var source = new MemoryStream("secret attachment"u8.ToArray());

            await studio.StoreAsync(
                new StorageWriteRequest("objects/1", source, "application/octet-stream", "1.bin", "user"));

            StorageException ex = await Assert.ThrowsAsync<StorageException>(
                () => portal.OpenReadAsync("objects/1"));

            Assert.Equal("STORAGE-OWNER-002", ex.Code);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Caller_cannot_spoof_different_application_owner()
    {
        string root = Path.Combine(Path.GetTempPath(), "common-storage-owner-" + Guid.NewGuid().ToString("N"));
        try
        {
            var raw = new LocalFileStorage(root);
            var studio = new ApplicationScopedFileStorage(raw, "Aegis.Studio");
            await using var source = new MemoryStream("x"u8.ToArray());

            StorageException ex = await Assert.ThrowsAsync<StorageException>(() =>
                studio.StoreAsync(new StorageWriteRequest(
                    "objects/2",
                    source,
                    "application/octet-stream",
                    "2.bin",
                    "user",
                    new Dictionary<string,string> { ["Application"] = "RequestPortal" })));

            Assert.Equal("STORAGE-OWNER-001", ex.Code);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
