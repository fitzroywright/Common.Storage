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

    public ApplicationScopedFileStorage(IFileStorage storage, string applicationId)
    {
        this.storage = storage ?? throw new ArgumentNullException(nameof(storage));
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationId);
        this.applicationId = applicationId.Trim();
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
        return await storage.StoreAsync(
            request with { Metadata = metadata },
            cancellationToken).ConfigureAwait(false);
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
        return await storage.OpenReadAsync(storageKey, cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteAsync(
        string storageKey,
        CancellationToken cancellationToken = default)
    {
        StoredFile? metadata = await storage.GetMetadataAsync(storageKey, cancellationToken).ConfigureAwait(false);
        if (metadata is null) return;
        EnsureOwned(metadata);
        await storage.DeleteAsync(storageKey, cancellationToken).ConfigureAwait(false);
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
