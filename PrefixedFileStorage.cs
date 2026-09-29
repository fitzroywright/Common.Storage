namespace Common.Storage;

/// <summary>
/// Presents a logical sub-root of another storage provider without exposing provider details
/// to the consuming feature. The stored StorageKey remains the feature-relative key.
/// </summary>
public sealed class PrefixedFileStorage : IFileStorage
{
    private readonly IFileStorage inner;
    private readonly string prefix;

    public PrefixedFileStorage(IFileStorage inner, string prefix)
    {
        this.inner = inner ?? throw new ArgumentNullException(nameof(inner));
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        this.prefix = Normalize(prefix);
    }

    public async Task<StoredFile> StoreAsync(StorageWriteRequest request, CancellationToken cancellationToken = default)
    {
        StoredFile stored = await inner.StoreAsync(
            request with { StorageKey = PhysicalKey(request.StorageKey) },
            cancellationToken).ConfigureAwait(false);
        return ToLogical(stored);
    }

    public Task<Stream> OpenReadAsync(string storageKey, CancellationToken cancellationToken = default) =>
        inner.OpenReadAsync(PhysicalKey(storageKey), cancellationToken);

    public async Task<StoredFile?> GetMetadataAsync(string storageKey, CancellationToken cancellationToken = default)
    {
        StoredFile? stored = await inner.GetMetadataAsync(PhysicalKey(storageKey), cancellationToken).ConfigureAwait(false);
        return stored is null ? null : ToLogical(stored);
    }

    public async Task<IReadOnlyList<StoredFile>> GetVersionsAsync(string storageKey, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<StoredFile> values = await inner.GetVersionsAsync(PhysicalKey(storageKey), cancellationToken).ConfigureAwait(false);
        return values.Select(ToLogical).ToArray();
    }

    public Task DeleteAsync(string storageKey, CancellationToken cancellationToken = default) =>
        inner.DeleteAsync(PhysicalKey(storageKey), cancellationToken);

    public Task<StorageHealth> CheckHealthAsync(CancellationToken cancellationToken = default) =>
        inner.CheckHealthAsync(cancellationToken);

    private string PhysicalKey(string key) => $"{prefix}/{Normalize(key)}";

    private StoredFile ToLogical(StoredFile stored)
    {
        string prefixWithSlash = prefix + "/";
        string key = stored.StorageKey.StartsWith(prefixWithSlash, StringComparison.OrdinalIgnoreCase)
            ? stored.StorageKey[prefixWithSlash.Length..]
            : stored.StorageKey;
        return stored with { StorageKey = key };
    }

    private static string Normalize(string value) =>
        value.Replace('\\', '/').Trim('/');
}
