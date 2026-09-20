using Common.Diagnostics;
using Xunit;

namespace Common.Storage.Tests;

public sealed class LifecycleStorageTelemetryTests
{
    [Fact]
    public async Task ScopedStorage_EmitsLifecycleWithoutStorageKey()
    {
        string root = Path.Combine(Path.GetTempPath(), "common-storage-lifecycle-" + Guid.NewGuid().ToString("N"));
        try
        {
            var sink = new RecordingSink();
            var raw = new LocalFileStorage(root);
            var scoped = new ApplicationScopedFileStorage(raw, "Aegis.Studio", "studio-01", sink);
            await using var source = new MemoryStream("hello"u8.ToArray());

            await scoped.StoreAsync(
                new StorageWriteRequest(
                    "private/customer-123/document.pdf",
                    source,
                    "application/pdf",
                    "document.pdf",
                    "user"));

            Assert.Equal(2, sink.Items.Count);
            Assert.Equal("Storage", sink.Items[0].Flow);
            Assert.Equal("Store", sink.Items[0].Stage);
            string json = System.Text.Json.JsonSerializer.Serialize(sink.Items);
            Assert.DoesNotContain("customer-123", json, StringComparison.Ordinal);
            Assert.DoesNotContain("document.pdf", json, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private sealed class RecordingSink : ILifecycleEventSink
    {
        public List<LifecycleEvent> Items { get; } = [];
        public Task EmitAsync(LifecycleEvent lifecycleEvent, CancellationToken cancellationToken = default)
        {
            Items.Add(lifecycleEvent);
            return Task.CompletedTask;
        }
    }
}
