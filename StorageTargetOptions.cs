using Microsoft.Extensions.Configuration;

namespace Common.Storage;

public enum StorageTargetKind
{
    LocalFolder,
    NetworkFolder,
    SharePoint
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
    SharePointStorageOptions? SharePoint = null)
{
    public static StorageTargetOptions FromConfiguration(
        IConfiguration configuration,
        string? sharePointClientSecret = null,
        string sectionName = "Storage")
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

        IConfigurationSection sharePoint = section.GetSection("SharePoint");
        string tenantId = Required(sharePoint, "TenantId", sectionName);
        string clientId = Required(sharePoint, "ClientId", sectionName);
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

    private static string Required(IConfigurationSection section, string key, string root)
    {
        string? value = section[key]?.Trim();
        return string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException($"{root}:SharePoint:{key} is required for SharePoint storage.")
            : value;
    }

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
