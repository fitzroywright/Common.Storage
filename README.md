# Common.Storage

Shared .NET 10 storage component for FFP applications.

## Implemented scope

- Stable `IFileStorage` contract for store/read/delete/metadata/version history/health.
- Local filesystem provider suitable for local disks and mounted SMB/NFS paths.
- Atomic publish using temporary files.
- SHA-256 calculated during upload and verified during reads.
- Immutable version history when a logical storage key is replaced.
- Metadata sidecars including original filename, content type, uploader, timestamp and custom metadata.
- Storage-root traversal protection.
- Explicit `StorageException` codes for diagnostics and operational failure handling.
- Writable health probe.
- Tests for normal operation, versioning, size limits, traversal, corruption and health.

## Deliberately excluded

Backup/restore is not implemented here. It remains an infrastructure/operations responsibility for the underlying storage target.

## Integration

Applications should reference `src/Common.Storage/Common.Storage.csproj` during local source development or consume the package when package publishing is enabled. Configure `LocalFileStorage` with an absolute local path or a mounted network-share path. Application services remain responsible for authorization and malware/content policy; Common.Storage is responsible for durable byte storage, integrity, version history and storage diagnostics.
