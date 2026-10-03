using System.Text;
using Xunit;

namespace Common.Storage.Tests;

public sealed class RealProviderContractTests
{
    [ExternalProviderFact("COMMON_STORAGE_SMB_ROOT", "Supply a real writable SMB/UNC or mounted SMB path.")]
    public Task NetworkFolder_RealProviderContract()
    {
        string root = Environment.GetEnvironmentVariable("COMMON_STORAGE_SMB_ROOT")!;
        return RunContractAsync(
            () => new NetworkFolderStorage(root),
            "SMB");
    }

    [ExternalProviderFact(
        "COMMON_STORAGE_SHAREPOINT_TENANT_ID,COMMON_STORAGE_SHAREPOINT_CLIENT_ID,COMMON_STORAGE_SHAREPOINT_CLIENT_SECRET",
        "Also configure either COMMON_STORAGE_SHAREPOINT_SITE_ID or COMMON_STORAGE_SHAREPOINT_HOST_NAME.")]
    public Task SharePoint_RealProviderContract()
    {
        string tenantId = Environment.GetEnvironmentVariable("COMMON_STORAGE_SHAREPOINT_TENANT_ID")!;
        string clientId = Environment.GetEnvironmentVariable("COMMON_STORAGE_SHAREPOINT_CLIENT_ID")!;
        string clientSecret = Environment.GetEnvironmentVariable("COMMON_STORAGE_SHAREPOINT_CLIENT_SECRET")!;
        string? siteId = Environment.GetEnvironmentVariable("COMMON_STORAGE_SHAREPOINT_SITE_ID");
        string? hostName = Environment.GetEnvironmentVariable("COMMON_STORAGE_SHAREPOINT_HOST_NAME");
        string sitePath = Environment.GetEnvironmentVariable("COMMON_STORAGE_SHAREPOINT_SITE_PATH") ?? string.Empty;
        string? driveId = Environment.GetEnvironmentVariable("COMMON_STORAGE_SHAREPOINT_DRIVE_ID");
        string? driveName = Environment.GetEnvironmentVariable("COMMON_STORAGE_SHAREPOINT_DRIVE_NAME");
        string rootFolder = Environment.GetEnvironmentVariable("COMMON_STORAGE_SHAREPOINT_ROOT_FOLDER") ?? "Aegis/Common.Storage.Proof";

        if (string.IsNullOrWhiteSpace(siteId) && string.IsNullOrWhiteSpace(hostName))
            throw new InvalidOperationException(
                "SharePoint proof requires either COMMON_STORAGE_SHAREPOINT_SITE_ID or COMMON_STORAGE_SHAREPOINT_HOST_NAME.");

        return RunContractAsync(
            () => new SharePointFileStorage(new SharePointStorageOptions(
                tenantId,
                clientId,
                clientSecret,
                siteId,
                hostName,
                sitePath,
                driveId,
                driveName,
                rootFolder)),
            "SharePoint");
    }

    [ExternalProviderFact(
        "COMMON_STORAGE_S3_ENDPOINT,COMMON_STORAGE_S3_BUCKET,COMMON_STORAGE_S3_ACCESS_KEY_ID,COMMON_STORAGE_S3_SECRET_ACCESS_KEY",
        "Configure a dedicated TrueNAS or other S3-compatible test target.")]
    public Task S3Compatible_RealProviderContract()
    {
        string bucket = Environment.GetEnvironmentVariable("COMMON_STORAGE_S3_BUCKET")!;
        string endpoint = Environment.GetEnvironmentVariable("COMMON_STORAGE_S3_ENDPOINT")!;
        string region = Environment.GetEnvironmentVariable("COMMON_STORAGE_S3_REGION") ?? "us-east-1";
        string accessKey = Environment.GetEnvironmentVariable("COMMON_STORAGE_S3_ACCESS_KEY_ID")!;
        string secretKey = Environment.GetEnvironmentVariable("COMMON_STORAGE_S3_SECRET_ACCESS_KEY")!;
        string rootFolder = Environment.GetEnvironmentVariable("COMMON_STORAGE_S3_ROOT_FOLDER") ?? "aegis/common-storage-proof";
        bool forcePathStyle = ReadBool("COMMON_STORAGE_S3_FORCE_PATH_STYLE", true);
        bool requireHttps = ReadBool("COMMON_STORAGE_S3_REQUIRE_HTTPS", true);

        return RunContractAsync(
            () => new S3FileStorage(new S3StorageOptions(
                bucket,
                region,
                rootFolder,
                accessKey,
                secretKey,
                endpoint,
                forcePathStyle,
                requireHttps)),
            "S3-compatible");
    }

    private static bool ReadBool(string name, bool fallback)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        return bool.TryParse(value, out bool parsed) ? parsed : fallback;
    }

    private static async Task RunContractAsync(
        Func<IFileStorage> createProvider,
        string providerName)
    {
        string prefix = $"proof/{DateTimeOffset.UtcNow:yyyyMMdd}/{Guid.NewGuid():N}";
        string key = $"{prefix}/contract.bin";
        byte[] firstPayload = Encoding.UTF8.GetBytes("common-storage-proof-v1");
        byte[] secondPayload = Encoding.UTF8.GetBytes("common-storage-proof-v2");

        IFileStorage storage = createProvider();
        try
        {
            StorageHealth health = await storage.CheckHealthAsync();
            Assert.True(health.Available, $"{providerName} is not available: {health.Error}");
            Assert.True(health.Writable, $"{providerName} is not writable: {health.Error}");

            StoredFile first = await StoreAsync(storage, key, firstPayload, "v1");
            Assert.Equal(1, first.Version);
            Assert.Equal("v1", first.Metadata["ProofVersion"]);
            await AssertContentAsync(storage, key, firstPayload);

            StoredFile? metadata = await storage.GetMetadataAsync(key);
            Assert.NotNull(metadata);
            Assert.Equal(first.Sha256, metadata!.Sha256);

            StoredFile second = await StoreAsync(storage, key, secondPayload, "v2");
            Assert.Equal(2, second.Version);
            Assert.Equal("v2", second.Metadata["ProofVersion"]);
            await AssertContentAsync(storage, key, secondPayload);

            IReadOnlyList<StoredFile> versions = await storage.GetVersionsAsync(key);
            Assert.Contains(versions, item => item.Version == 1);
            Assert.Contains(versions, item => item.Version == 2);

            IStorageQuery query = Assert.IsAssignableFrom<IStorageQuery>(storage);
            IReadOnlyList<StoredFile> listed = await query.ListAsync(new StorageListRequest(prefix, 10));
            Assert.Contains(listed, item => item.StorageKey == key && item.Version == 2);

            IStorageMetadataEditor metadataEditor = Assert.IsAssignableFrom<IStorageMetadataEditor>(storage);
            StoredFile edited = await metadataEditor.UpdateMetadataAsync(
                key,
                new StorageMetadataUpdate(
                    new Dictionary<string, string>
                    {
                        ["ProofEdited"] = "true"
                    }));
            Assert.Equal(2, edited.Version);
            Assert.Equal("true", edited.Metadata["ProofEdited"]);

            IReadOnlyList<StoredFile> historicalAfterEdit = await storage.GetVersionsAsync(key);
            StoredFile historicalVersionTwo = Assert.Single(historicalAfterEdit, item => item.Version == 2);
            Assert.False(historicalVersionTwo.Metadata.ContainsKey("ProofEdited"));

            // Recreate the provider to prove identity/persistence is not in-memory only.
            storage = createProvider();
            StoredFile? afterReconnect = await storage.GetMetadataAsync(key);
            Assert.NotNull(afterReconnect);
            Assert.Equal(2, afterReconnect!.Version);
            Assert.Equal("true", afterReconnect.Metadata["ProofEdited"]);
            await AssertContentAsync(storage, key, secondPayload);

            await storage.DeleteAsync(key);
            Assert.Null(await storage.GetMetadataAsync(key));
            await Assert.ThrowsAsync<StorageException>(() => storage.OpenReadAsync(key));
        }
        finally
        {
            try { await storage.DeleteAsync(key); } catch { }
        }
    }

    private static async Task<StoredFile> StoreAsync(
        IFileStorage storage,
        string key,
        byte[] payload,
        string version)
    {
        await using MemoryStream content = new(payload, writable: false);
        return await storage.StoreAsync(new StorageWriteRequest(
            key,
            content,
            "application/octet-stream",
            "contract.bin",
            "Common.Storage.Proof",
            new Dictionary<string, string>
            {
                ["ProofVersion"] = version,
                ["ProofRun"] = "RealProviderContractTests"
            }));
    }

    private static async Task AssertContentAsync(
        IFileStorage storage,
        string key,
        byte[] expected)
    {
        await using Stream stream = await storage.OpenReadAsync(key);
        using MemoryStream copy = new();
        await stream.CopyToAsync(copy);
        Assert.Equal(expected, copy.ToArray());
    }
}
