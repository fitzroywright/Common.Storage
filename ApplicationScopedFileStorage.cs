using Common.Diagnostics;

namespace Common.Storage;

/// <summary>
/// Application-scoped storage facade. It enforces a neutral owner identifier in metadata and
/// refuses cross-owner metadata/read/delete operations. Business authorization remains with the
/// consuming application.
/// </summary>
public sealed class ApplicationScopedFileStorage
{
    public const string ApplicationMetadataKey = "Application";

    private readonly IFileStorage storage;
    private readonly string applicationId;
    private readonly string instanceId;
    private readonly ILifecycleEventSink? lifecycle;

    public ApplicationScopedFileStorage(
        IFileStorage storage,
        string applicationId,
        string? instanceId = null,
        ILifecycleEventSink? lifecycle = null)
    {
        this.storage = storage ?? throw new ArgumentNullException(nameof(storage));
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationId);
        this.applicationId = applicationId.Trim();
        this.instanceId = string.IsNullOrWhiteSpace(instanceId) ? Environment.MachineName : instanceId.Trim();
        this.lifecycle = lifecycle;
    }

    public async Task<StoredFile> StoreAsync(
        StorageWriteRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var metadata = request.Metadata is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(request.Metadata, StringComparer.OrdinalIgnoreCase);

        if (metadata.TryGetValue(ApplicationMetadataKey, out string? declared) &&
            !string.Equals(declared, applicationId, StringComparison.OrdinalIgnoreCase))
        {
            throw new StorageException(
                "STORAGE-OWNER-001",
                "Storage metadata declares a different application owner.");
        }

        metadata[ApplicationMetadataKey] = applicationId;
        string correlationId = Guid.NewGuid().ToString("N");
        await EmitAsync("Store", LifecycleEventOutcome.Started, correlationId, cancellationToken).ConfigureAwait(false);
        try
        {
            StoredFile stored = await storage.StoreAsync(
                request with { Metadata = metadata },
                cancellationToken).ConfigureAwait(false);
            await EmitAsync("Store", LifecycleEventOutcome.Succeeded, correlationId, cancellationToken).ConfigureAwait(false);
            return stored;
        }
        catch
        {
            await EmitAsync("Store", LifecycleEventOutcome.Failed, correlationId, cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<StoredFile?> GetMetadataAsync(
        string storageKey,
        CancellationToken cancellationToken = default)
    {
        StoredFile? metadata = await storage.GetMetadataAsync(storageKey, cancellationToken).ConfigureAwait(false);
        if (metadata is null) return null;
        EnsureOwned(metadata);
        return metadata;
    }

    public async Task<Stream> OpenReadAsync(
        string storageKey,
        CancellationToken cancellationToken = default)
    {
        StoredFile? metadata = await storage.GetMetadataAsync(storageKey, cancellationToken).ConfigureAwait(false);
        if (metadata is null)
            throw new StorageException("STORAGE-MISSING-001", $"Stored file '{storageKey}' does not exist.");

        EnsureOwned(metadata);
        string correlationId = Guid.NewGuid().ToString("N");
        await EmitAsync("OpenRead", LifecycleEventOutcome.Started, correlationId, cancellationToken).ConfigureAwait(false);
        try
        {
            Stream stream = await storage.OpenReadAsync(storageKey, cancellationToken).ConfigureAwait(false);
            await EmitAsync("OpenRead", LifecycleEventOutcome.Succeeded, correlationId, cancellationToken).ConfigureAwait(false);
            return stream;
        }
        catch
        {
            await EmitAsync("OpenRead", LifecycleEventOutcome.Failed, correlationId, cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    public async Task DeleteAsync(
        string storageKey,
        CancellationToken cancellationToken = default)
    {
        StoredFile? metadata = await storage.GetMetadataAsync(storageKey, cancellationToken).ConfigureAwait(false);
        if (metadata is null) return;
        EnsureOwned(metadata);
        string correlationId = Guid.NewGuid().ToString("N");
        await EmitAsync("Delete", LifecycleEventOutcome.Started, correlationId, cancellationToken).ConfigureAwait(false);
        try
        {
            await storage.DeleteAsync(storageKey, cancellationToken).ConfigureAwait(false);
            await EmitAsync("Delete", LifecycleEventOutcome.Succeeded, correlationId, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await EmitAsync("Delete", LifecycleEventOutcome.Failed, correlationId, cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    private async Task EmitAsync(
        string stage,
        LifecycleEventOutcome outcome,
        string correlationId,
        CancellationToken cancellationToken)
    {
        if (lifecycle is null) return;
        try
        {
            await lifecycle.EmitAsync(
                LifecycleEvent.Create(
                    applicationId,
                    instanceId,
                    "Storage",
                    stage,
                    outcome,
                    correlationId),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Storage behavior must not depend on telemetry availability.
        }
    }

    private void EnsureOwned(StoredFile metadata)
    {
        if (!metadata.Metadata.TryGetValue(ApplicationMetadataKey, out string? owner) ||
            !string.Equals(owner, applicationId, StringComparison.OrdinalIgnoreCase))
        {
            throw new StorageException(
                "STORAGE-OWNER-002",
                "Stored object does not belong to the current application scope.");
        }
    }
}
