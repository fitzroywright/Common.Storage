namespace Common.Storage;

public sealed class NetworkFolderStorage :
    IVersionedFileStorage,
    IStorageMaintenance,
    IStorageLifecycle
{
    private readonly string rootPath;

    public NetworkFolderStorage(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        this.rootPath = rootPath;
    }

    public Task<StoredFile> StoreAsync(StorageWriteRequest request, CancellationToken cancellationToken = default) =>
        CreateInner().StoreAsync(request, cancellationToken);

    public Task<Stream> OpenReadAsync(string storageKey, CancellationToken cancellationToken = default) =>
        CreateInner().OpenReadAsync(storageKey, cancellationToken);

    public Task<StoredFile?> GetMetadataAsync(string storageKey, CancellationToken cancellationToken = default) =>
        CreateInner().GetMetadataAsync(storageKey, cancellationToken);

    public Task<IReadOnlyList<StoredFile>> GetVersionsAsync(string storageKey, CancellationToken cancellationToken = default) =>
        CreateInner().GetVersionsAsync(storageKey, cancellationToken);

    public Task DeleteAsync(string storageKey, CancellationToken cancellationToken = default) =>
        CreateInner().DeleteAsync(storageKey, cancellationToken);

    public async Task<StorageHealth> CheckHealthAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            LocalFileStorage inner = CreateInner();
            StorageHealth health = await inner.CheckHealthAsync(cancellationToken).ConfigureAwait(false);
            return health with
            {
                Provider = nameof(NetworkFolderStorage),
                Root = rootPath
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new StorageHealth(
                false,
                false,
                nameof(NetworkFolderStorage),
                rootPath,
                ex.Message);
        }
    }

    public Task<Stream> OpenVersionAsync(string storageKey, int version, CancellationToken cancellationToken = default) =>
        CreateInner().OpenVersionAsync(storageKey, version, cancellationToken);

    public Task<StoredFile> RestoreVersionAsync(string storageKey, int version, string restoredBy, CancellationToken cancellationToken = default) =>
        CreateInner().RestoreVersionAsync(storageKey, version, restoredBy, cancellationToken);

    public Task<StorageMaintenanceResult> RunMaintenanceAsync(StorageMaintenanceOptions options, CancellationToken cancellationToken = default) =>
        CreateInner().RunMaintenanceAsync(options, cancellationToken);

    public Task<StoragePurgeResult> PurgeAsync(string storageKey, CancellationToken cancellationToken = default) =>
        CreateInner().PurgeAsync(storageKey, cancellationToken);

    private LocalFileStorage CreateInner()
    {
        try
        {
            return new LocalFileStorage(rootPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new StorageException(
                "STORAGE-NETWORK-UNAVAILABLE-001",
                $"Network storage root '{rootPath}' is unavailable.",
                ex);
        }
    }
}
