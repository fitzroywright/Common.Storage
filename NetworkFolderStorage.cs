namespace Common.Storage;

public sealed class NetworkFolderStorage :
    IVersionedFileStorage,
    IStorageMaintenance,
    IStorageLifecycle
{
    private readonly LocalFileStorage inner;
    private readonly string rootPath;

    public NetworkFolderStorage(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        this.rootPath = rootPath;
        inner = new LocalFileStorage(rootPath);
    }

    public Task<StoredFile> StoreAsync(StorageWriteRequest request, CancellationToken cancellationToken = default) =>
        inner.StoreAsync(request, cancellationToken);

    public Task<Stream> OpenReadAsync(string storageKey, CancellationToken cancellationToken = default) =>
        inner.OpenReadAsync(storageKey, cancellationToken);

    public Task<StoredFile?> GetMetadataAsync(string storageKey, CancellationToken cancellationToken = default) =>
        inner.GetMetadataAsync(storageKey, cancellationToken);

    public Task<IReadOnlyList<StoredFile>> GetVersionsAsync(string storageKey, CancellationToken cancellationToken = default) =>
        inner.GetVersionsAsync(storageKey, cancellationToken);

    public Task DeleteAsync(string storageKey, CancellationToken cancellationToken = default) =>
        inner.DeleteAsync(storageKey, cancellationToken);

    public async Task<StorageHealth> CheckHealthAsync(CancellationToken cancellationToken = default)
    {
        StorageHealth health = await inner.CheckHealthAsync(cancellationToken).ConfigureAwait(false);
        return health with
        {
            Provider = nameof(NetworkFolderStorage),
            Root = rootPath
        };
    }

    public Task<Stream> OpenVersionAsync(string storageKey, int version, CancellationToken cancellationToken = default) =>
        inner.OpenVersionAsync(storageKey, version, cancellationToken);

    public Task<StoredFile> RestoreVersionAsync(string storageKey, int version, string restoredBy, CancellationToken cancellationToken = default) =>
        inner.RestoreVersionAsync(storageKey, version, restoredBy, cancellationToken);

    public Task<StorageMaintenanceResult> RunMaintenanceAsync(StorageMaintenanceOptions options, CancellationToken cancellationToken = default) =>
        inner.RunMaintenanceAsync(options, cancellationToken);

    public Task<StoragePurgeResult> PurgeAsync(string storageKey, CancellationToken cancellationToken = default) =>
        inner.PurgeAsync(storageKey, cancellationToken);
}
