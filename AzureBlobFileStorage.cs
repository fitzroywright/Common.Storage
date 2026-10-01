using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using System.Net;

namespace Common.Storage;

public sealed class AzureBlobFileStorage : IVersionedFileStorage
{
    private readonly BlobContainerClient container;
    private readonly string rootFolder;

    public AzureBlobFileStorage(AzureBlobStorageOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.ConnectionString))
            throw new InvalidOperationException("Azure Blob storage requires a connection string.");
        if (string.IsNullOrWhiteSpace(options.ContainerName))
            throw new InvalidOperationException("Azure Blob storage requires a container name.");
        container = new BlobContainerClient(options.ConnectionString, options.ContainerName);
        rootFolder = options.RootFolder?.Trim('/') ?? string.Empty;
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

        await container.CreateIfNotExistsAsync(cancellationToken: cancellationToken);
        BlobClient blob = container.GetBlobClient(CloudStorageSupport.Prefix(rootFolder, key));
        await using MemoryStream stream = new(bytes, writable: false);
        await blob.UploadAsync(stream, new BlobUploadOptions
        {
            HttpHeaders = new BlobHttpHeaders { ContentType = stored.ContentType }
        }, cancellationToken);
        BlobClient versionBlob = container.GetBlobClient(CloudStorageSupport.VersionMetadataKey(rootFolder, key, version).Replace(".json", ".bin", StringComparison.Ordinal));
        await versionBlob.UploadAsync(new MemoryStream(bytes, writable: false), overwrite: true, cancellationToken);

        await PutMetadataAsync(CloudStorageSupport.VersionMetadataKey(rootFolder, key, version), stored, cancellationToken);
        await PutMetadataAsync(CloudStorageSupport.MetadataKey(rootFolder, key), stored, cancellationToken);
        return stored;
    }

    public async Task<Stream> OpenReadAsync(string storageKey, CancellationToken cancellationToken = default)
    {
        string key = CloudStorageSupport.NormalizeKey(storageKey);
        StoredFile? metadata = await GetMetadataAsync(key, cancellationToken);
        if (metadata is null)
            throw new StorageException("STORAGE-MISSING-001", $"Stored file '{key}' does not exist.");

        BlobClient blob = container.GetBlobClient(CloudStorageSupport.Prefix(rootFolder, key));
        try
        {
            BlobDownloadInfo download = await blob.DownloadAsync(cancellationToken);
            return await CopyToMemoryAsync(download.Content, cancellationToken);
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == (int)HttpStatusCode.NotFound)
        {
            throw new StorageException("STORAGE-MISSING-001", $"Stored file '{key}' does not exist.", ex);
        }
    }

    public async Task<Stream> OpenVersionAsync(string storageKey, int version, CancellationToken cancellationToken = default)
    {
        if (version <= 0) throw new ArgumentOutOfRangeException(nameof(version));
        string key = CloudStorageSupport.NormalizeKey(storageKey);
        StoredFile? metadata = await GetVersionMetadataAsync(key, version, cancellationToken);
        if (metadata is null)
            throw new StorageException("STORAGE-VERSION-MISSING-001", $"Version {version} of '{key}' does not exist.");

        BlobClient blob = container.GetBlobClient(CloudStorageSupport.VersionMetadataKey(rootFolder, key, version).Replace(".json", ".bin", StringComparison.Ordinal));
        try
        {
            BlobDownloadInfo download = await blob.DownloadAsync(cancellationToken);
            return await CopyToMemoryAsync(download.Content, cancellationToken);
        }
        catch (Azure.RequestFailedException ex)
        {
            throw new StorageException("STORAGE-VERSION-READ-001", $"Unable to read version {version} of '{key}'.", ex);
        }
    }

    public async Task<StoredFile> RestoreVersionAsync(string storageKey, int version, string restoredBy, CancellationToken cancellationToken = default)
    {
        await using Stream source = await OpenVersionAsync(storageKey, version, cancellationToken);
        StoredFile metadata = await GetVersionMetadataAsync(storageKey, version, cancellationToken)
            ?? throw new StorageException("STORAGE-VERSION-MISSING-001", $"Version {version} does not exist.");
        Dictionary<string,string> restored = new(metadata.Metadata, StringComparer.OrdinalIgnoreCase)
        {
            ["RestoredFromVersion"] = version.ToString(System.Globalization.CultureInfo.InvariantCulture)
        };
        return await StoreAsync(new StorageWriteRequest(
            storageKey, source, metadata.ContentType, metadata.OriginalFileName, restoredBy, restored), cancellationToken);
    }

    public async Task<StoredFile?> GetMetadataAsync(string storageKey, CancellationToken cancellationToken = default)
    {
        string key = CloudStorageSupport.NormalizeKey(storageKey);
        return await GetJsonMetadataAsync(CloudStorageSupport.MetadataKey(rootFolder, key), cancellationToken);
    }

    public async Task<IReadOnlyList<StoredFile>> GetVersionsAsync(string storageKey, CancellationToken cancellationToken = default)
    {
        string key = CloudStorageSupport.NormalizeKey(storageKey);
        string prefix = CloudStorageSupport.VersionMetadataPrefix(rootFolder, key);
        List<StoredFile> values = [];
        await foreach (BlobItem item in container.GetBlobsAsync(prefix: prefix, cancellationToken: cancellationToken))
        {
            StoredFile? value = await GetJsonMetadataAsync(item.Name, cancellationToken);
            if (value is not null) values.Add(value);
        }
        return values.OrderByDescending(item => item.Version).ToArray();
    }

    public async Task DeleteAsync(string storageKey, CancellationToken cancellationToken = default)
    {
        string key = CloudStorageSupport.NormalizeKey(storageKey);
        await container.GetBlobClient(CloudStorageSupport.Prefix(rootFolder, key))
            .DeleteIfExistsAsync(DeleteSnapshotsOption.IncludeSnapshots, cancellationToken);
        await container.GetBlobClient(CloudStorageSupport.MetadataKey(rootFolder, key))
            .DeleteIfExistsAsync(cancellationToken: cancellationToken);
    }

    public async Task<StorageHealth> CheckHealthAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await container.CreateIfNotExistsAsync(cancellationToken: cancellationToken);
            string key = CloudStorageSupport.Prefix(rootFolder, "__health/" + Guid.NewGuid().ToString("N"));
            BlobClient blob = container.GetBlobClient(key);
            await blob.UploadAsync(BinaryData.FromString("ok"), overwrite: true, cancellationToken);
            await blob.DeleteIfExistsAsync(cancellationToken: cancellationToken);
            return new StorageHealth(true, true, nameof(AzureBlobFileStorage), container.Uri.ToString());
        }
        catch (Exception ex) when (ex is Azure.RequestFailedException or InvalidOperationException)
        {
            return new StorageHealth(false, false, nameof(AzureBlobFileStorage), container.Uri.ToString(), ex.Message);
        }
    }

    private async Task PutMetadataAsync(string key, StoredFile value, CancellationToken cancellationToken)
    {
        BlobClient blob = container.GetBlobClient(key);
        await blob.UploadAsync(BinaryData.FromString(CloudStorageSupport.Serialize(value)), overwrite: true, cancellationToken);
    }

    private async Task<StoredFile?> GetVersionMetadataAsync(string key, int version, CancellationToken cancellationToken) =>
        await GetJsonMetadataAsync(CloudStorageSupport.VersionMetadataKey(rootFolder, key, version), cancellationToken);

    private async Task<StoredFile?> GetJsonMetadataAsync(string key, CancellationToken cancellationToken)
    {
        BlobClient blob = container.GetBlobClient(key);
        try
        {
            BlobDownloadInfo download = await blob.DownloadAsync(cancellationToken);
            BinaryData data = await BinaryData.FromStreamAsync(download.Content);
            return CloudStorageSupport.Deserialize(data.ToString());
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == (int)HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    private static async Task<MemoryStream> CopyToMemoryAsync(Stream source, CancellationToken cancellationToken)
    {
        MemoryStream target = new();
        await source.CopyToAsync(target, cancellationToken);
        target.Position = 0;
        return target;
    }
}
