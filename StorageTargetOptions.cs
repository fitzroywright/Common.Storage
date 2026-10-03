using Microsoft.Extensions.Configuration;

namespace Common.Storage;

public enum StorageTargetKind
{
    LocalFolder,
    NetworkFolder,
    SharePoint,
    AzureBlob,
    S3,
    AmazonS3
}

public sealed record AzureBlobStorageOptions(
    string ConnectionString,
    string ContainerName,
    string RootFolder = "");

public sealed record S3StorageOptions(
    string BucketName,
    string Region,
    string RootFolder = "",
    string? AccessKeyId = null,
    string? SecretAccessKey = null,
    string? Endpoint = null,
    bool ForcePathStyle = true,
    bool RequireHttps = true);

public sealed record AmazonS3StorageOptions(
    string BucketName,
    string Region,
    string RootFolder = "",
    string? AccessKeyId = null,
    string? SecretAccessKey = null,
    string? ServiceUrl = null)
{
    internal S3StorageOptions ToS3Options() =>
        new(
            BucketName,
            Region,
            RootFolder,
            AccessKeyId,
            SecretAccessKey,
            ServiceUrl,
            ForcePathStyle: !string.IsNullOrWhiteSpace(ServiceUrl),
            RequireHttps: false);
}

public sealed record SharePointStorageOptions(
    string TenantId,
    string ClientId,
    string ClientSecret,
    string? SiteId,
    string? HostName,
    string SitePath,
    string? DriveId = null,
    string? DriveName = null,
    string RootFolder = "");

public sealed record StorageTargetOptions(
    StorageTargetKind Kind,
    string? RootPath = null,
    SharePointStorageOptions? SharePoint = null,
    AzureBlobStorageOptions? AzureBlob = null,
    S3StorageOptions? S3 = null,
    AmazonS3StorageOptions? AmazonS3 = null)
{
    public static StorageTargetOptions FromConfiguration(
        IConfiguration configuration,
        string? sharePointClientSecret = null,
        string sectionName = "Storage",
        string? s3AccessKeyId = null,
        string? s3SecretAccessKey = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        IConfigurationSection section = configuration.GetSection(sectionName);
        string provider = section["Provider"]?.Trim() ?? "LocalFolder";
        if (!Enum.TryParse(provider, true, out StorageTargetKind kind))
            throw new InvalidOperationException($"Unsupported storage provider '{provider}'.");

        if (kind is StorageTargetKind.LocalFolder or StorageTargetKind.NetworkFolder)
        {
            string key = kind == StorageTargetKind.NetworkFolder
                ? "NetworkFolder:RootPath"
                : "Local:RootPath";
            string? root = section[key]?.Trim();
            if (string.IsNullOrWhiteSpace(root))
                throw new InvalidOperationException($"{sectionName}:{key} is required for {kind} storage.");
            return new StorageTargetOptions(kind, root);
        }

        if (kind == StorageTargetKind.AzureBlob)
        {
            IConfigurationSection azure = section.GetSection("AzureBlob");
            string connectionString = sharePointClientSecret?.Trim() ?? azure["ConnectionString"]?.Trim() ?? string.Empty;
            string container = Required(azure, "ContainerName", sectionName, "AzureBlob");
            return new StorageTargetOptions(
                kind,
                AzureBlob: new AzureBlobStorageOptions(
                    connectionString,
                    container,
                    Clean(azure["RootFolder"]) ?? string.Empty));
        }

        if (kind == StorageTargetKind.S3)
        {
            IConfigurationSection s3 = section.GetSection("S3");
            S3StorageOptions options = ReadS3(
                s3,
                sectionName,
                s3AccessKeyId,
                s3SecretAccessKey ?? sharePointClientSecret);
            return new StorageTargetOptions(kind, S3: options);
        }

        if (kind == StorageTargetKind.AmazonS3)
        {
            IConfigurationSection legacy = section.GetSection("AmazonS3");
            string bucket = RequiredAny(legacy, ["BucketName", "Bucket"], sectionName, "AmazonS3");
            string region = Clean(legacy["Region"]) ?? "us-east-1";
            AmazonS3StorageOptions options = new(
                bucket,
                region,
                Clean(legacy["RootFolder"]) ?? string.Empty,
                s3AccessKeyId ?? Clean(legacy["AccessKeyId"]),
                s3SecretAccessKey ?? sharePointClientSecret?.Trim() ?? Clean(legacy["SecretAccessKey"]),
                Clean(legacy["ServiceUrl"]) ?? Clean(legacy["Endpoint"]));
            return new StorageTargetOptions(kind, AmazonS3: options);
        }

        IConfigurationSection sharePoint = section.GetSection("SharePoint");
        string tenantId = Required(sharePoint, "TenantId", sectionName, "SharePoint");
        string clientId = Required(sharePoint, "ClientId", sectionName, "SharePoint");
        string? siteId = Clean(sharePoint["SiteId"]);
        string? hostName = Clean(sharePoint["HostName"]);
        string sitePath = sharePoint["SitePath"]?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(siteId) && string.IsNullOrWhiteSpace(hostName))
            throw new InvalidOperationException($"{sectionName}:SharePoint requires SiteId or HostName.");
        string secret = sharePointClientSecret?.Trim() ?? sharePoint["ClientSecret"]?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(secret))
            throw new InvalidOperationException($"{sectionName}:SharePoint client secret is required through the consuming application's secret provider.");

        return new StorageTargetOptions(
            kind,
            SharePoint: new SharePointStorageOptions(
                tenantId,
                clientId,
                secret,
                siteId,
                hostName,
                sitePath,
                Clean(sharePoint["DriveId"]),
                Clean(sharePoint["DriveName"]),
                Clean(sharePoint["RootFolder"]) ?? string.Empty));
    }

    private static S3StorageOptions ReadS3(
        IConfigurationSection section,
        string root,
        string? resolvedAccessKey,
        string? resolvedSecretKey)
    {
        string bucket = RequiredAny(section, ["Bucket", "BucketName"], root, "S3");
        string region = Clean(section["Region"]) ?? "us-east-1";
        string? endpoint = Clean(section["Endpoint"]) ?? Clean(section["ServiceUrl"]);
        bool forcePathStyle = section.GetValue<bool?>("ForcePathStyle")
            ?? !string.IsNullOrWhiteSpace(endpoint);
        bool requireHttps = section.GetValue<bool?>("RequireHttps") ?? true;

        if (!string.IsNullOrWhiteSpace(endpoint))
        {
            if (!Uri.TryCreate(endpoint, UriKind.Absolute, out Uri? endpointUri))
                throw new InvalidOperationException($"{root}:S3:Endpoint must be an absolute URI.");
            if (requireHttps && endpointUri.Scheme != Uri.UriSchemeHttps)
                throw new InvalidOperationException($"{root}:S3:Endpoint must use HTTPS when RequireHttps=true.");
        }

        return new S3StorageOptions(
            bucket,
            region,
            Clean(section["RootFolder"]) ?? string.Empty,
            resolvedAccessKey ?? Clean(section["AccessKeyId"]),
            resolvedSecretKey?.Trim() ?? Clean(section["SecretAccessKey"]),
            endpoint,
            forcePathStyle,
            requireHttps);
    }

    private static string Required(
        IConfigurationSection section,
        string key,
        string root,
        string provider)
    {
        string? value = section[key]?.Trim();
        return string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException($"{root}:{provider}:{key} is required for {provider} storage.")
            : value;
    }

    private static string RequiredAny(
        IConfigurationSection section,
        string[] keys,
        string root,
        string provider)
    {
        foreach (string key in keys)
        {
            string? value = Clean(section[key]);
            if (value is not null) return value;
        }

        throw new InvalidOperationException(
            $"{root}:{provider}:{string.Join(" or ", keys)} is required for {provider} storage.");
    }

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
