# Common.Storage

Shared .NET 10 storage component for application file storage.

## Implemented scope

- Stable `IFileStorage` contract for store/read/delete/metadata/version history/health.
- `IVersionedFileStorage` for opening historical versions and restoring an older version as a new current version.
- `IStorageMaintenance` for stale temporary-file cleanup and configurable version retention.
- `IStorageLifecycle` for an explicit irreversible purge that removes the current object and all historical versions.
- Local filesystem provider suitable for local disks and mounted SMB/NFS paths whose filesystem semantics honor exclusive file locking.
- Bounded streaming with a 128 KiB working buffer; uploads are not loaded into memory as one large byte array.
- In-process and filesystem-backed per-key serialization so concurrent writers, including separate provider instances/processes sharing the same root, receive distinct immutable versions.
- Atomic-style publication using temporary files and replacement of the current logical file only after a completed write.
- Cleanup of interrupted `.uploading`, `.writing`, and `.replacing` temporary files without deleting files that are still actively open by a writer.
- SHA-256 calculated during upload and verified during current and historical reads.
- Immutable version history when a logical storage key is replaced.
- Metadata sidecars including original filename, content type, uploader, timestamp and custom metadata.
- Storage-root traversal protection plus rejection of symbolic-link/reparse-point paths and filesystem-unsafe key characters.
- Explicit `StorageException` codes for diagnostics and operational failure handling.
- Storage health probe that proves write and read-back, plus ASP.NET Core `common-storage` health-check registration.
- Opt-in hosted maintenance worker for periodic cleanup and version retention.
- Tests covering normal operation, cross-instance concurrency, delete/write serialization, historical restore, bounded streaming, size limits, traversal, symlink escape, corruption, provider disappearance, maintenance, purge and health registration.

## Registration

```csharp
string storageRoot = configuration["Storage:RootPath"]
    ?? throw new InvalidOperationException("Storage:RootPath is required.");

services.AddCommonStorage(storageRoot);
```

`AddCommonStorage` registers one shared `LocalFileStorage` instance as `IFileStorage`, `IVersionedFileStorage`, `IStorageMaintenance`, and `IStorageLifecycle`, and adds the `common-storage` health check.

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

The hosted worker runs once on startup and then at the configured interval. It removes stale interrupted-write files and, when configured, prunes historical versions beyond the retained count. Version pruning obtains the same filesystem-backed lock used by writers so maintenance cannot race publication of a version. The current published version is not deleted by version-history maintenance.

## Delete versus purge

`IFileStorage.DeleteAsync` removes the current logical file and current metadata but deliberately preserves immutable historical versions. This supports ordinary application deletion/withdrawal without silently destroying recovery history.

`IStorageLifecycle.PurgeAsync` is deliberately separate because it is destructive. It removes the current file, current metadata, all historical version binaries and metadata, and abandoned temporary artifacts for the logical key. Use purge only when the application's retention/legal policy explicitly requires irreversible removal.

```csharp
IStorageLifecycle lifecycle = services.GetRequiredService<IStorageLifecycle>();
StoragePurgeResult result = await lifecycle.PurgeAsync("documents/123/file.pdf");
```

## Failure and integrity expectations

- A failed or oversized upload must not publish a current file.
- A failed pre-commit version write cleans incomplete binary/metadata artifacts so it does not poison later version numbering.
- Concurrent writers to the same key use both an in-process semaphore and a filesystem-backed exclusive lock.
- Delete, purge, and retention pruning coordinate with the same per-key/version lock so they do not race an active publication.
- Current and historical reads verify SHA-256 before returning the stream.
- Missing/unavailable storage surfaces through `StorageException` or storage health rather than silently falling back to another location.
- Logical keys cannot escape the configured root with `..`, absolute-path tricks, symbolic links or reparse points.
- Keys containing control characters or filesystem-unsafe characters such as `:`, `*`, `?`, `"`, `<`, `>` and `|` are rejected for consistent Windows/Linux behavior.
- A healthy provider must successfully write and read back its health probe.
- Deleting a logical current file does not silently erase immutable historical versions; purge is the explicit irreversible operation.

## Shared filesystem note

The local provider uses exclusive file handles for cross-process serialization. This is appropriate for local filesystems and mounted SMB/NFS targets only when the mount/server honors exclusive file-lock semantics consistently. Before using a shared NAS path for multiple application nodes, run an integration test against the exact production mount and failover configuration.

## Consumer responsibilities

Common.Storage stores bytes and storage metadata. The application remains responsible for:

- user/application authorization;
- malware scanning and content-type/file-policy validation;
- business retention policy and legal holds;
- deciding when version pruning or irreversible purge is permitted;
- backup, replication and disaster recovery of the underlying storage target;
- encryption-at-rest policy for the filesystem or storage appliance.

RequestPortal already layers upload validation, malware scanning, authorization, business retention metadata and auditing above Common.Storage. Studio uses Common.Storage for attachment persistence. Consumers should prefer DI-resolved `IFileStorage` rather than constructing a provider directly so health, maintenance and future provider substitution remain centralized.

## Backup and restore

Infrastructure-level backup/restore remains an operations responsibility for the underlying storage target. `IVersionedFileStorage.RestoreVersionAsync` is application-level logical version recovery and is not a substitute for filesystem/NAS backup.
