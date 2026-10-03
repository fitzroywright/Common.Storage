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

## Gaps that prevent declaring Common.Storage fully proven

The current public `IFileStorage` contract does **not** expose:

1. provider-neutral enumeration/list/query of objects;
2. standalone metadata update without rewriting the object.

Those are explicit requirements in the comprehensive proof package, so they cannot truthfully be marked proven by the current contract suite. Adding them should be done as additive provider-neutral contracts rather than leaking SharePoint, filesystem, or S3 semantics into applications.

Permission-denied, authentication-failure, timeout/network-interruption and read-only-target proofs also require controlled real-provider fixtures and credentials. They must remain real integration tests rather than mocks if they are to satisfy the proof criteria.
