namespace Common.Storage;

/// <summary>
/// Backward-compatible adapter for deployments still configured as AmazonS3.
/// New deployments should use the provider-neutral S3 target and <see cref="S3FileStorage"/>.
/// </summary>
public sealed class AmazonS3FileStorage : IVersionedFileStorage
{
    private readonly S3FileStorage inner;

    public AmazonS3FileStorage(AmazonS3StorageOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        inner = new S3FileStorage(options.ToS3Options());
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

    public Task<StorageHealth> CheckHealthAsync(CancellationToken cancellationToken = default) =>
        inner.CheckHealthAsync(cancellationToken);

    public Task<Stream> OpenVersionAsync(string storageKey, int version, CancellationToken cancellationToken = default) =>
        inner.OpenVersionAsync(storageKey, version, cancellationToken);

    public Task<StoredFile> RestoreVersionAsync(string storageKey, int version, string restoredBy, CancellationToken cancellationToken = default) =>
        inner.RestoreVersionAsync(storageKey, version, restoredBy, cancellationToken);
}
