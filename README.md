# Common.Storage

Shared .NET 10 storage component for application file storage.

## Implemented scope

- Stable `IFileStorage` contract for store/read/delete/metadata/version history/health.
- `IVersionedFileStorage` for opening historical versions and restoring an older version as a new current version.
- `IStorageMaintenance` for stale temporary-file cleanup and configurable version retention.
- Local filesystem provider suitable for local disks and mounted SMB/NFS paths.
- Bounded streaming with a 128 KiB working buffer; uploads are not loaded into memory as one large byte array.
- Per-key serialization so concurrent writes to the same logical key become distinct immutable versions.
- Atomic-style publication using temporary files and replacement of the current logical file only after a completed write.
- Cleanup of interrupted `.uploading`, `.writing`, and `.replacing` temporary files.
- SHA-256 calculated during upload and verified during current and historical reads.
- Immutable version history when a logical storage key is replaced.
- Metadata sidecars including original filename, content type, uploader, timestamp and custom metadata.
- Storage-root traversal protection plus rejection of symbolic-link/reparse-point paths inside the storage root.
- Explicit `StorageException` codes for diagnostics and operational failure handling.
- Writable storage health probe and ASP.NET Core `common-storage` health-check registration.
- Opt-in hosted maintenance worker for periodic cleanup and version retention.
- Tests covering normal operation, concurrency, historical restore, bounded streaming, size limits, traversal, symlink escape, corruption, provider disappearance, maintenance and health registration.

## Registration

```csharp
string storageRoot = configuration["Storage:RootPath"]
    ?? throw new InvalidOperationException("Storage:RootPath is required.");

services.AddCommonStorage(storageRoot);
```

`AddCommonStorage` registers one shared `LocalFileStorage` instance as `IFileStorage`, `IVersionedFileStorage`, and `IStorageMaintenance`, and adds the `common-storage` health check.

Applications that expose ASP.NET Core health checks can map them normally:

```csharp
app.MapHealthChecks("/health");
```

## Periodic maintenance

Maintenance is deliberately opt-in because retention requirements belong to the consuming application.

```csharp
services.AddCommonStorageMaintenance(
    new StorageMaintenanceHostedOptions(
        TimeSpan.FromHours(6),
        new StorageMaintenanceOptions(
            StaleTemporaryFileAge: TimeSpan.FromHours(24),
            RetainLatestVersions: 10)));
```

The hosted worker runs once on startup and then at the configured interval. It removes stale interrupted-write files and, when configured, prunes historical versions beyond the retained count. The current published version is not deleted by version-history maintenance.

## Failure and integrity expectations

- A failed or oversized upload must not publish a current file.
- Concurrent writes to one key must receive distinct monotonically increasing versions within a provider instance.
- Current and historical reads verify SHA-256 before returning the stream.
- Missing/unavailable storage surfaces through `StorageException` or storage health rather than silently falling back to another location.
- Logical keys cannot escape the configured root with `..`, absolute-path tricks, symbolic links or reparse points.
- Deleting a logical current file does not silently erase immutable historical versions.

## Consumer responsibilities

Common.Storage stores bytes and storage metadata. The application remains responsible for:

- user/application authorization;
- malware scanning and content-type/file-policy validation;
- business retention policy and legal holds;
- deciding when version pruning is permitted;
- backup, replication and disaster recovery of the underlying storage target;
- encryption-at-rest policy for the filesystem or storage appliance.

RequestPortal already layers upload validation, malware scanning, authorization, business retention metadata and auditing above Common.Storage. Studio uses Common.Storage for attachment persistence. Consumers should prefer DI-resolved `IFileStorage` rather than constructing a provider directly so health, maintenance and future provider substitution remain centralized.

## Backup and restore

Infrastructure-level backup/restore remains an operations responsibility for the underlying storage target. `IVersionedFileStorage.RestoreVersionAsync` is application-level logical version recovery and is not a substitute for filesystem/NAS backup.
