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

    public async Task<IReadOnlyList<StoredFile>> ListAsync(
        StorageListRequest? request = null,
        CancellationToken cancellationToken = default)
    {
        if (storage is not IStorageQuery query)
            throw new StorageException("STORAGE-CAPABILITY-001", "The configured storage provider does not support object enumeration.");

        StorageListRequest resolved = request ?? new StorageListRequest();
        IReadOnlyList<StoredFile> values = await query.ListAsync(resolved, cancellationToken).ConfigureAwait(false);
        return values
            .Where(IsOwned)
            .Take(resolved.MaximumResults)
            .ToArray();
    }

    public async Task<StoredFile> UpdateMetadataAsync(
        string storageKey,
        StorageMetadataUpdate update,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        if (storage is not IStorageMetadataEditor editor)
            throw new StorageException("STORAGE-CAPABILITY-002", "The configured storage provider does not support metadata updates.");

        StoredFile? current = await storage.GetMetadataAsync(storageKey, cancellationToken).ConfigureAwait(false);
        if (current is null)
            throw new StorageException("STORAGE-MISSING-001", $"Stored file '{storageKey}' does not exist.");
        EnsureOwned(current);

        Dictionary<string, string> metadata = new(update.Metadata, StringComparer.OrdinalIgnoreCase)
        {
            [ApplicationMetadataKey] = applicationId
        };
        return await editor.UpdateMetadataAsync(
            storageKey,
            update with { Metadata = metadata },
            cancellationToken).ConfigureAwait(false);
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

    private bool IsOwned(StoredFile metadata) =>
        metadata.Metadata.TryGetValue(ApplicationMetadataKey, out string? owner) &&
        string.Equals(owner, applicationId, StringComparison.OrdinalIgnoreCase);

    private void EnsureOwned(StoredFile metadata)
    {
        if (!IsOwned(metadata))
        {
            throw new StorageException(
                "STORAGE-OWNER-002",
                "Stored object does not belong to the current application scope.");
        }
    }
}
