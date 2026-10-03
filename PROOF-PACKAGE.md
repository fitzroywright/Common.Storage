# Common.Storage proof package

This repository now carries an independently runnable real-provider contract in
`Common.Storage.Tests/RealProviderContractTests.cs`.

## What the automated real-provider contract proves

For SMB, SharePoint, and optional S3-compatible targets the same provider-neutral test performs:

- health/reachability and write readiness;
- create/store;
- read-back and byte-for-byte content integrity;
- metadata persistence;
- overwrite/version creation;
- version metadata retrieval;
- provider re-instantiation followed by lookup/read, proving persistence is not only in-memory;
- delete and post-delete missing behavior;
- provider-neutral prefix enumeration;
- standalone current-metadata update without content rewrite or version creation;
- persistence of metadata edits across provider re-instantiation;
- preservation of immutable historical version metadata after current-metadata edits;
- cleanup of the proof object.

The tests use the public `IFileStorage` contract. No application-specific behavior is required.

## Running the real SMB proof

Set:

`COMMON_STORAGE_SMB_ROOT`

to a real writable UNC path or mounted SMB path, then run:

`dotnet test Common.Storage.Tests/Common.Storage.Tests.csproj --filter NetworkFolder_RealProviderContract`

When the variable is absent the test is reported as skipped with the missing requirement.

## Running the real SharePoint proof

Set:

- `COMMON_STORAGE_SHAREPOINT_TENANT_ID`
- `COMMON_STORAGE_SHAREPOINT_CLIENT_ID`
- `COMMON_STORAGE_SHAREPOINT_CLIENT_SECRET`
- either `COMMON_STORAGE_SHAREPOINT_SITE_ID` or `COMMON_STORAGE_SHAREPOINT_HOST_NAME`
- optional `COMMON_STORAGE_SHAREPOINT_SITE_PATH`
- optional `COMMON_STORAGE_SHAREPOINT_DRIVE_ID`
- optional `COMMON_STORAGE_SHAREPOINT_DRIVE_NAME`
- optional `COMMON_STORAGE_SHAREPOINT_ROOT_FOLDER`

Then run:

`dotnet test Common.Storage.Tests/Common.Storage.Tests.csproj --filter SharePoint_RealProviderContract`

Use a dedicated test folder/library location. The proof creates temporary objects and deletes them.

## Running the optional S3-compatible proof

Set:

- `COMMON_STORAGE_S3_BUCKET`
- optional `COMMON_STORAGE_S3_ENDPOINT` for TrueNAS, MinIO, or another compatible endpoint
- optional `COMMON_STORAGE_S3_REGION` (defaults to `us-east-1`)
- optional `COMMON_STORAGE_S3_ACCESS_KEY_ID`
- optional `COMMON_STORAGE_S3_SECRET_ACCESS_KEY`
- optional `COMMON_STORAGE_S3_ROOT_FOLDER`

If access-key variables are omitted, the AWS SDK's normal credential chain is used.

## DiagnosticLevel coverage already present

The current code already registers:

- root-exists checks for filesystem providers;
- root-readable checks;
- free-space checks;
- a Level 3 write/read/verify/delete round-trip;
- `CommonStorageDiagnosticCheck`;
- `CommonStorageDependencyProbe`;
- the ASP.NET Core `common-storage` health check.

## Remaining proof gaps

Provider-neutral enumeration is exposed through `IStorageQuery`, and standalone current-metadata editing is exposed through `IStorageMetadataEditor`. Both are additive capabilities so existing `IFileStorage` implementations remain source-compatible.

Permission-denied, authentication-failure, timeout/network-interruption and read-only-target proofs still require controlled real-provider fixtures and credentials. They must remain real integration tests rather than mocks if they are to satisfy the proof criteria.


## Current proof status (2026-10-02)

Completed with real providers:

- baseline Common.Storage suite green;
- real SMB/TrueNAS contract passed against `\\nas01\public\temp`;
- SMB unavailable-path failure reproduced and recovery to the same real share passed;
- real SharePoint/Microsoft Graph contract passed;
- SharePoint invalid-secret failure produced `STORAGE-SHAREPOINT-AUTH-001` and recovery with the restored secret passed;
- real S3-compatible contract passed against TrueNAS/SeaweedFS;
- the real-provider contract now also proves prefix enumeration and independent metadata update for supported targets.

Hardening added after the SMB unavailable-path proof:

- `NetworkFolderStorage` no longer requires the remote root to be reachable during construction;
- unavailable network roots are surfaced through `StorageHealth` as unavailable/not writable;
- storage operations translate an unavailable network root to `StorageException` code `STORAGE-NETWORK-UNAVAILABLE-001`;
- regression coverage protects the provider-neutral failure behavior.

Intentionally deferred:

- SMB reachable-but-read-only / permission-denied proof;
- SharePoint valid identity with insufficient site/drive authorization;
- broader interruption/timeout fault injection beyond the observed transient Graph timeout and successful retry.

These deferred authorization tests are not counted as passed.
