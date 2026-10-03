# S3-compatible storage

Common.Storage supports provider-neutral S3-compatible object storage through `Storage:Provider=S3`.

Example:

```json
{
  "Storage": {
    "Provider": "S3",
    "S3": {
      "Endpoint": "https://truenas.example.internal:9000",
      "Region": "us-east-1",
      "Bucket": "aegis-media",
      "RootFolder": "Aegis/Studio",
      "ForcePathStyle": true,
      "RequireHttps": true
    }
  }
}
```

Credentials should be resolved by the consuming application's Common.Secrets configuration and passed into `StorageTargetOptions.FromConfiguration` through `s3AccessKeyId` and `s3SecretAccessKey`. Do not place them in source or committed appsettings.

`Endpoint` is optional for AWS-style default endpoints but is expected for TrueNAS, MinIO, Synology or other compatible targets. `ForcePathStyle=true` is the safe default for custom endpoints. Set `RequireHttps=false` only for an intentionally HTTP-only local endpoint.

The legacy `AmazonS3` target remains available for existing deployments. New consumers should use `S3`.

Named targets can be parsed without changing consuming business logic by passing a section such as `Storage:Profiles:Media` to `StorageTargetOptions.FromConfiguration`. Applications are not required to use multiple targets yet.

## Live provider proof

The opt-in live test uses:

- `COMMON_STORAGE_S3_ENDPOINT`
- `COMMON_STORAGE_S3_BUCKET`
- `COMMON_STORAGE_S3_ACCESS_KEY_ID`
- `COMMON_STORAGE_S3_SECRET_ACCESS_KEY`
- optional `COMMON_STORAGE_S3_REGION` (default `us-east-1`)
- optional `COMMON_STORAGE_S3_ROOT_FOLDER`
- optional `COMMON_STORAGE_S3_FORCE_PATH_STYLE` (default `true`)
- optional `COMMON_STORAGE_S3_REQUIRE_HTTPS` (default `true`)

Run:

```powershell
dotnet test Common.Storage.Tests\Common.Storage.Tests.csproj -c Release --filter "S3Compatible_RealProviderContract"
```

If the required environment variables are absent, the test is skipped.
