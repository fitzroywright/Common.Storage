using Amazon;
using Amazon.S3;
using Amazon.S3.Model;
using System.Net;

namespace Common.Storage;

public sealed class S3FileStorage : IVersionedFileStorage, IStorageQuery, IStorageMetadataEditor, IStorageBucketReset
{
    private readonly IAmazonS3 client;
    private readonly string bucket;
    private readonly string rootFolder;

    public S3FileStorage(S3StorageOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.BucketName))
            throw new InvalidOperationException("S3-compatible storage requires BucketName.");
        if (string.IsNullOrWhiteSpace(options.Region))
            throw new InvalidOperationException("S3-compatible storage requires Region.");

        bucket = options.BucketName.Trim();
        rootFolder = options.RootFolder?.Trim('/') ?? string.Empty;

        AmazonS3Config config = new()
        {
            RegionEndpoint = RegionEndpoint.GetBySystemName(options.Region)
        };
        if (!string.IsNullOrWhiteSpace(options.Endpoint))
        {
            if (!Uri.TryCreate(options.Endpoint, UriKind.Absolute, out Uri? endpoint))
                throw new InvalidOperationException("S3-compatible storage Endpoint must be an absolute URI.");
            if (options.RequireHttps && endpoint.Scheme != Uri.UriSchemeHttps)
                throw new InvalidOperationException("S3-compatible storage Endpoint must use HTTPS when RequireHttps=true.");

            config.ServiceURL = endpoint.AbsoluteUri.TrimEnd('/');
            config.ForcePathStyle = options.ForcePathStyle;
        }

        client = string.IsNullOrWhiteSpace(options.AccessKeyId) || string.IsNullOrWhiteSpace(options.SecretAccessKey)
            ? new AmazonS3Client(config)
            : new AmazonS3Client(options.AccessKeyId, options.SecretAccessKey, config);
    }

    public async Task<StoredFile> StoreAsync(StorageWriteRequest request, CancellationToken cancellationToken = default)
    {
        string key = CloudStorageSupport.NormalizeKey(request.StorageKey);
        (byte[] bytes, long length, string sha256) = await CloudStorageSupport.ReadAndHashAsync(request.Content, request.MaximumBytes, cancellationToken);
        StoredFile? current = await GetMetadataAsync(key, cancellationToken);
        int version = CloudStorageSupport.NextVersion(current);
        StoredFile stored = new(
            key,
            length,
            string.IsNullOrWhiteSpace(request.ContentType) ? "application/octet-stream" : request.ContentType.Trim(),
            Path.GetFileName(request.OriginalFileName),
            sha256,
            DateTimeOffset.UtcNow,
            string.IsNullOrWhiteSpace(request.UploadedBy) ? "Unknown" : request.UploadedBy.Trim(),
            version,
            StorageStatus.Stored,
            StorageMetadata.Freeze(request.Metadata));

        await PutAsync(CloudStorageSupport.Prefix(rootFolder, key), bytes, stored.ContentType, cancellationToken);
        await PutAsync(
            CloudStorageSupport.VersionMetadataKey(rootFolder, key, version).Replace(".json", ".bin", StringComparison.Ordinal),
            bytes,
            stored.ContentType,
            cancellationToken);
        await PutMetadataAsync(CloudStorageSupport.VersionMetadataKey(rootFolder, key, version), stored, cancellationToken);
        await PutMetadataAsync(CloudStorageSupport.MetadataKey(rootFolder, key), stored, cancellationToken);
        return stored;
    }

    public async Task<Stream> OpenReadAsync(string storageKey, CancellationToken cancellationToken = default)
    {
        string key = CloudStorageSupport.NormalizeKey(storageKey);
        StoredFile? metadata = await GetMetadataAsync(key, cancellationToken);
        if (metadata is null) throw new StorageException("STORAGE-MISSING-001", $"Stored file '{key}' does not exist.");
        return await ReadToMemoryAsync(CloudStorageSupport.Prefix(rootFolder, key), cancellationToken);
    }

    public async Task<Stream> OpenVersionAsync(string storageKey, int version, CancellationToken cancellationToken = default)
    {
        if (version <= 0) throw new ArgumentOutOfRangeException(nameof(version));
        string key = CloudStorageSupport.NormalizeKey(storageKey);
        StoredFile? metadata = await GetVersionMetadataAsync(key, version, cancellationToken);
        if (metadata is null) throw new StorageException("STORAGE-VERSION-MISSING-001", $"Version {version} of '{key}' does not exist.");
        return await ReadToMemoryAsync(CloudStorageSupport.VersionMetadataKey(rootFolder, key, version).Replace(".json", ".bin", StringComparison.Ordinal), cancellationToken);
    }

    public async Task<StoredFile> RestoreVersionAsync(string storageKey, int version, string restoredBy, CancellationToken cancellationToken = default)
    {
        StoredFile metadata = await GetVersionMetadataAsync(storageKey, version, cancellationToken)
            ?? throw new StorageException("STORAGE-VERSION-MISSING-001", $"Version {version} does not exist.");
        await using Stream source = await OpenVersionAsync(storageKey, version, cancellationToken);
        Dictionary<string,string> restored = new(metadata.Metadata, StringComparer.OrdinalIgnoreCase)
        {
            ["RestoredFromVersion"] = version.ToString(System.Globalization.CultureInfo.InvariantCulture)
        };
        return await StoreAsync(new StorageWriteRequest(storageKey, source, metadata.ContentType, metadata.OriginalFileName, restoredBy, restored), cancellationToken);
    }

    public async Task<StoredFile?> GetMetadataAsync(string storageKey, CancellationToken cancellationToken = default) =>
        await GetJsonMetadataAsync(CloudStorageSupport.MetadataKey(rootFolder, CloudStorageSupport.NormalizeKey(storageKey)), cancellationToken);

    public async Task<IReadOnlyList<StoredFile>> GetVersionsAsync(string storageKey, CancellationToken cancellationToken = default)
    {
        string key = CloudStorageSupport.NormalizeKey(storageKey);
        List<StoredFile> values = [];
        string prefix = CloudStorageSupport.VersionMetadataPrefix(rootFolder, key);
        ListObjectsV2Response response;
        string? token = null;
        do
        {
            response = await client.ListObjectsV2Async(new ListObjectsV2Request
            {
                BucketName = bucket,
                Prefix = prefix,
                ContinuationToken = token
            }, cancellationToken);
            // Some S3-compatible providers return a successful empty listing with a
            // null S3Objects collection. Treat that as an empty page rather than
            // allowing LINQ to throw while callers are recovering a missing version.
            foreach (S3Object item in (response.S3Objects ?? []).Where(item =>
                         !string.IsNullOrWhiteSpace(item.Key) &&
                         item.Key.EndsWith(".json", StringComparison.OrdinalIgnoreCase)))
            {
                StoredFile? value = await GetJsonMetadataAsync(item.Key, cancellationToken);
                if (value is not null) values.Add(value);
            }
            token = response.IsTruncated == true ? response.NextContinuationToken : null;
        } while (token is not null);
        return values.OrderByDescending(item => item.Version).ToArray();
    }

    public async Task<IReadOnlyList<StoredFile>> ListAsync(
        StorageListRequest? request = null,
        CancellationToken cancellationToken = default)
    {
        StorageListRequest resolved = request ?? new StorageListRequest();
        if (resolved.MaximumResults <= 0)
            throw new ArgumentOutOfRangeException(nameof(request), "MaximumResults must be positive.");

        string? logicalPrefix = string.IsNullOrWhiteSpace(resolved.Prefix)
            ? null
            : CloudStorageSupport.NormalizeKey(resolved.Prefix);
        string metadataPrefix = CloudStorageSupport.Prefix(rootFolder, "__commonstorage/metadata") + "/";

        List<StoredFile> values = [];
        string? token = null;
        do
        {
            ListObjectsV2Response response = await client.ListObjectsV2Async(new ListObjectsV2Request
            {
                BucketName = bucket,
                Prefix = metadataPrefix,
                ContinuationToken = token
            }, cancellationToken);

            foreach (S3Object item in (response.S3Objects ?? []).Where(item =>
                         !string.IsNullOrWhiteSpace(item.Key) &&
                         item.Key.EndsWith(".json", StringComparison.OrdinalIgnoreCase)))
            {
                StoredFile? value = await GetJsonMetadataAsync(item.Key, cancellationToken);
                if (value is null)
                    continue;
                if (logicalPrefix is not null &&
                    !value.StorageKey.StartsWith(logicalPrefix, StringComparison.OrdinalIgnoreCase))
                    continue;

                values.Add(value);
                if (values.Count >= resolved.MaximumResults)
                    return values.OrderBy(item => item.StorageKey, StringComparer.OrdinalIgnoreCase).ToArray();
            }

            token = response.IsTruncated == true ? response.NextContinuationToken : null;
        } while (token is not null);

        return values.OrderBy(item => item.StorageKey, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public async Task<StoredFile> UpdateMetadataAsync(
        string storageKey,
        StorageMetadataUpdate update,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        ArgumentNullException.ThrowIfNull(update.Metadata);

        string key = CloudStorageSupport.NormalizeKey(storageKey);
        StoredFile current = await GetMetadataAsync(key, cancellationToken)
            ?? throw new StorageException("STORAGE-MISSING-001", $"Stored file '{key}' does not exist.");

        Dictionary<string, string> metadata = update.Replace
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(current.Metadata, StringComparer.OrdinalIgnoreCase);
        foreach ((string name, string value) in update.Metadata)
            metadata[name] = value;

        StoredFile updated = current with { Metadata = StorageMetadata.Freeze(metadata) };
        await PutMetadataAsync(CloudStorageSupport.MetadataKey(rootFolder, key), updated, cancellationToken);
        return updated;
    }

    public async Task DeleteAsync(string storageKey, CancellationToken cancellationToken = default)
    {
        string key = CloudStorageSupport.NormalizeKey(storageKey);
        await client.DeleteObjectAsync(bucket, CloudStorageSupport.Prefix(rootFolder, key), cancellationToken);
        await client.DeleteObjectAsync(bucket, CloudStorageSupport.MetadataKey(rootFolder, key), cancellationToken);
    }

    /// <summary>
    /// Remove every object in this dedicated bucket, including S3 version IDs and
    /// delete markers. This intentionally ignores RootFolder because legacy/orphan
    /// Studio objects can exist outside the configured logical prefix.
    /// Requires ListBucket, ListBucketVersions, DeleteObject and DeleteObjectVersion.
    /// </summary>
    public async Task<long> PurgeBucketAsync(CancellationToken cancellationToken = default)
    {
        long deleted = 0;
        var previousVersionBatch = new HashSet<string>(StringComparer.Ordinal);
        // A versioned bucket needs explicit version-ID deletion: deleting the
        // current key would merely create another delete marker.
        for (;;)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = await client.ListVersionsAsync(new ListVersionsRequest
            {
                BucketName = bucket,
                MaxKeys = 1000
            }, cancellationToken);
            var versions = (page.Versions ?? []).Select(v => (v.Key, v.VersionId))
                .Where(v => !string.IsNullOrWhiteSpace(v.Key) && !string.IsNullOrWhiteSpace(v.VersionId))
                .ToArray();
            if (versions.Length == 0) break;
            var versionBatch = versions.Select(v => v.Key + ":" + v.VersionId).ToHashSet(StringComparer.Ordinal);
            if (previousVersionBatch.SetEquals(versionBatch))
                throw new StorageException("STORAGE-RESET-002", "S3 version deletion made no progress.");
            previousVersionBatch = versionBatch;
            foreach (var version in versions)
            {
                await client.DeleteObjectAsync(new DeleteObjectRequest
                {
                    BucketName = bucket, Key = version.Key, VersionId = version.VersionId
                }, cancellationToken);
                deleted++;
            }
        }

        // Handle unversioned S3-compatible backends (including SeaweedFS)
        // and any current objects omitted by their version-list implementation.
        var previousObjectBatch = new HashSet<string>(StringComparer.Ordinal);
        for (;;)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = await client.ListObjectsV2Async(new ListObjectsV2Request
            {
                BucketName = bucket,
                MaxKeys = 1000
            }, cancellationToken);
            var keys = (page.S3Objects ?? []).Select(v => v.Key)
                .Where(key => !string.IsNullOrWhiteSpace(key)).ToArray();
            if (keys.Length == 0) break;
            if (previousObjectBatch.SetEquals(keys))
                throw new StorageException("STORAGE-RESET-003", "S3 object deletion made no progress.");
            previousObjectBatch = keys.ToHashSet(StringComparer.Ordinal);
            foreach (var key in keys)
            {
                await client.DeleteObjectAsync(bucket, key, cancellationToken);
                deleted++;
            }
        }

        var remaining = await client.ListObjectsV2Async(new ListObjectsV2Request
        {
            BucketName = bucket, MaxKeys = 1
        }, cancellationToken);
        var remainingVersions = await client.ListVersionsAsync(new ListVersionsRequest
        {
            BucketName = bucket, MaxKeys = 1
        }, cancellationToken);
        if ((remaining.S3Objects?.Count ?? 0) != 0 ||
            (remainingVersions.Versions?.Count ?? 0) != 0 ||
            false)
            throw new StorageException("STORAGE-RESET-001",
                "S3 bucket is not empty after factory reset. Retry after resolving permissions or storage errors.");
        return deleted;
    }

    public async Task<StorageHealth> CheckHealthAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            string key = CloudStorageSupport.Prefix(rootFolder, "__health/" + Guid.NewGuid().ToString("N"));
            await PutAsync(key, [1, 2, 3], "application/octet-stream", cancellationToken);
            await client.DeleteObjectAsync(bucket, key, cancellationToken);
            return new StorageHealth(true, true, nameof(S3FileStorage), bucket);
        }
        catch (AmazonS3Exception ex)
        {
            return new StorageHealth(false, false, nameof(S3FileStorage), bucket, ex.Message);
        }
    }

    private async Task PutMetadataAsync(string key, StoredFile value, CancellationToken cancellationToken) =>
        await PutAsync(key, System.Text.Encoding.UTF8.GetBytes(CloudStorageSupport.Serialize(value)), "application/json", cancellationToken);

    private async Task PutAsync(string key, byte[] bytes, string contentType, CancellationToken cancellationToken)
    {
        await using MemoryStream stream = new(bytes, writable: false);
        await client.PutObjectAsync(new PutObjectRequest
        {
            BucketName = bucket,
            Key = key,
            InputStream = stream,
            ContentType = contentType
        }, cancellationToken);
    }

    private async Task<StoredFile?> GetVersionMetadataAsync(string key, int version, CancellationToken cancellationToken) =>
        await GetJsonMetadataAsync(CloudStorageSupport.VersionMetadataKey(rootFolder, CloudStorageSupport.NormalizeKey(key), version), cancellationToken);

    private async Task<StoredFile?> GetJsonMetadataAsync(string key, CancellationToken cancellationToken)
    {
        try
        {
            GetObjectResponse response = await client.GetObjectAsync(bucket, key, cancellationToken);
            await using Stream stream = response.ResponseStream;
            using StreamReader reader = new(stream);
            return CloudStorageSupport.Deserialize(await reader.ReadToEndAsync(cancellationToken));
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    private async Task<MemoryStream> ReadToMemoryAsync(string key, CancellationToken cancellationToken)
    {
        try
        {
            GetObjectResponse response = await client.GetObjectAsync(bucket, key, cancellationToken);
            MemoryStream stream = new();
            await response.ResponseStream.CopyToAsync(stream, cancellationToken);
            stream.Position = 0;
            return stream;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            throw new StorageException("STORAGE-MISSING-001", $"Stored object '{key}' does not exist.", ex);
        }
    }
}
