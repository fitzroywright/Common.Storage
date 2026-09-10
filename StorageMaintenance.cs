namespace Common.Storage;

public sealed record StorageMaintenanceOptions(
    TimeSpan StaleTemporaryFileAge,
    int? RetainLatestVersions = null)
{
    public static StorageMaintenanceOptions Default { get; } = new(TimeSpan.FromHours(24));
}

public sealed record StorageMaintenanceResult(
    int TemporaryFilesRemoved,
    int VersionsRemoved,
    long BytesReclaimed);

public sealed record StoragePurgeResult(
    bool CurrentFileRemoved,
    int VersionsRemoved,
    long BytesReclaimed);

public interface IVersionedFileStorage : IFileStorage
{
    Task<Stream> OpenVersionAsync(string storageKey, int version, CancellationToken cancellationToken = default);
    Task<StoredFile> RestoreVersionAsync(string storageKey, int version, string restoredBy, CancellationToken cancellationToken = default);
}

public interface IStorageMaintenance
{
    Task<StorageMaintenanceResult> RunMaintenanceAsync(StorageMaintenanceOptions options, CancellationToken cancellationToken = default);
}

public interface IStorageLifecycle
{
    Task<StoragePurgeResult> PurgeAsync(string storageKey, CancellationToken cancellationToken = default);
}
