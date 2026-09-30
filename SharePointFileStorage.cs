using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Common.Storage;

public sealed class SharePointFileStorage : IFileStorage
{
    private const string GraphBase = "https://graph.microsoft.com/v1.0";
    private const int BufferSize = 128 * 1024;

    private readonly SharePointStorageOptions options;
    private readonly HttpClient httpClient;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> keyLocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim targetLock = new(1, 1);
    private readonly SemaphoreSlim tokenLock = new(1, 1);

    private string? siteId;
    private string? driveId;
    private string? accessToken;
    private DateTimeOffset tokenExpiresUtc;

    public SharePointFileStorage(SharePointStorageOptions options)
        : this(options, new HttpClient())
    {
    }

    public SharePointFileStorage(SharePointStorageOptions options, HttpClient httpClient)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        ValidateOptions(options);
    }

    public async Task<StoredFile> StoreAsync(StorageWriteRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Content);
        if (!request.Content.CanRead)
            throw new StorageException("STORAGE-INPUT-001", "Content stream is not readable.");

        string key = NormalizeKey(request.StorageKey);
        SemaphoreSlim keyLock = keyLocks.GetOrAdd(key, static _ => new SemaphoreSlim(1, 1));
        await keyLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? temporaryPath = null;
        try
        {
            temporaryPath = Path.Combine(Path.GetTempPath(), "common-storage", Guid.NewGuid().ToString("N") + ".upload");
            Directory.CreateDirectory(Path.GetDirectoryName(temporaryPath)!);
            (long length, string sha256) = await CopyAndHashAsync(
                request.Content,
                temporaryPath,
                request.MaximumBytes,
                cancellationToken).ConfigureAwait(false);

            StoredFile? current = await GetMetadataCoreAsync(key, cancellationToken).ConfigureAwait(false);
            int version = checked((current?.Version ?? 0) + 1);
            DateTimeOffset storedUtc = DateTimeOffset.UtcNow;
            StoredFile stored = new(
                key,
                length,
                NormalizeContentType(request.ContentType),
                Path.GetFileName(request.OriginalFileName),
                sha256,
                storedUtc,
                string.IsNullOrWhiteSpace(request.UploadedBy) ? "Unknown" : request.UploadedBy.Trim(),
                version,
                StorageStatus.Stored,
                StorageMetadata.Freeze(request.Metadata));

            await EnsureTargetAsync(cancellationToken).ConfigureAwait(false);
            await EnsureFolderPathAsync(ParentFolder(ContentPath(key)), cancellationToken).ConfigureAwait(false);

            GraphStoredItem graphItem;
            await using (FileStream upload = new(
                temporaryPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                BufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                graphItem = await PutContentAsync(
                    ContentPath(key),
                    upload,
                    stored.ContentType,
                    cancellationToken).ConfigureAwait(false);
            }

            Dictionary<string,string> providerMetadata = new(stored.Metadata, StringComparer.OrdinalIgnoreCase)
            {
                ["StorageProvider"] = "SharePoint",
                ["StorageDriveId"] = driveId!,
                ["StorageItemId"] = graphItem.ItemId
            };
            if (!string.IsNullOrWhiteSpace(graphItem.ETag))
                providerMetadata["StorageETag"] = graphItem.ETag;
            stored = stored with { Metadata = StorageMetadata.Freeze(providerMetadata) };

            string versionMetadataPath = VersionMetadataPath(key, version);
            await EnsureFolderPathAsync(ParentFolder(versionMetadataPath), cancellationToken).ConfigureAwait(false);
            await PutJsonAsync(versionMetadataPath, stored, cancellationToken).ConfigureAwait(false);

            string currentMetadataPath = CurrentMetadataPath(key);
            await EnsureFolderPathAsync(ParentFolder(currentMetadataPath), cancellationToken).ConfigureAwait(false);
            await PutJsonAsync(currentMetadataPath, stored, cancellationToken).ConfigureAwait(false);

            return stored;
        }
        catch (StorageException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or HttpRequestException or JsonException)
        {
            throw new StorageException("STORAGE-SHAREPOINT-WRITE-001", $"Unable to store '{key}' in SharePoint.", exception);
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(temporaryPath))
            {
                try { File.Delete(temporaryPath); } catch { }
            }
            keyLock.Release();
        }
    }

    public async Task<Stream> OpenReadAsync(string storageKey, CancellationToken cancellationToken = default)
    {
        string key = NormalizeKey(storageKey);
        StoredFile? metadata = await GetMetadataCoreAsync(key, cancellationToken).ConfigureAwait(false);
        if (metadata is null)
            throw new StorageException("STORAGE-MISSING-001", $"Stored file '{key}' does not exist.");

        await EnsureTargetAsync(cancellationToken).ConfigureAwait(false);
        HttpResponseMessage response = await SendGraphAsync(
            HttpMethod.Get,
            DrivePathContentUrl(ContentPath(key)),
            null,
            cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            response.Dispose();
            throw new StorageException("STORAGE-MISSING-001", $"Stored file '{key}' does not exist.");
        }

        if (!response.IsSuccessStatusCode)
        {
            string detail = await SafeReadBodyAsync(response, cancellationToken).ConfigureAwait(false);
            response.Dispose();
            throw new StorageException("STORAGE-SHAREPOINT-READ-001", $"SharePoint read failed for '{key}': {detail}");
        }

        string tempRoot = Path.Combine(Path.GetTempPath(), "common-storage", "downloads");
        Directory.CreateDirectory(tempRoot);
        string tempPath = Path.Combine(tempRoot, Guid.NewGuid().ToString("N") + ".download");
        try
        {
            await using Stream source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using (FileStream output = new(
                tempPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.Read,
                BufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await source.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            response.Dispose();
        }

        string actualHash = await ComputeHashAsync(tempPath, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(actualHash, metadata.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            try { File.Delete(tempPath); } catch { }
            throw new StorageException("STORAGE-CORRUPT-001", $"Stored file '{key}' failed SHA-256 verification.");
        }

        return new FileStream(
            tempPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.DeleteOnClose);
    }

    public Task<StoredFile?> GetMetadataAsync(string storageKey, CancellationToken cancellationToken = default) =>
        GetMetadataCoreAsync(NormalizeKey(storageKey), cancellationToken);

    public async Task<IReadOnlyList<StoredFile>> GetVersionsAsync(string storageKey, CancellationToken cancellationToken = default)
    {
        string key = NormalizeKey(storageKey);
        await EnsureTargetAsync(cancellationToken).ConfigureAwait(false);
        string folder = VersionMetadataFolder(key);
        HttpResponseMessage response = await SendGraphAsync(
            HttpMethod.Get,
            DriveChildrenUrl(folder),
            null,
            cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            response.Dispose();
            return [];
        }

        if (!response.IsSuccessStatusCode)
        {
            string detail = await SafeReadBodyAsync(response, cancellationToken).ConfigureAwait(false);
            response.Dispose();
            throw new StorageException("STORAGE-SHAREPOINT-VERSIONS-001", $"Unable to enumerate versions for '{key}': {detail}");
        }

        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        response.Dispose();

        List<StoredFile> versions = [];
        if (!document.RootElement.TryGetProperty("value", out JsonElement values))
            return versions;

        foreach (JsonElement item in values.EnumerateArray())
        {
            if (!item.TryGetProperty("name", out JsonElement nameElement))
                continue;
            string? name = nameElement.GetString();
            if (string.IsNullOrWhiteSpace(name) || !name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                continue;

            StoredFile? metadata = await GetJsonAsync<StoredFile>(
                CombinePath(folder, name),
                cancellationToken).ConfigureAwait(false);
            if (metadata is not null)
                versions.Add(metadata);
        }

        return versions.OrderByDescending(x => x.Version).ToArray();
    }

    public async Task DeleteAsync(string storageKey, CancellationToken cancellationToken = default)
    {
        string key = NormalizeKey(storageKey);
        SemaphoreSlim keyLock = keyLocks.GetOrAdd(key, static _ => new SemaphoreSlim(1, 1));
        await keyLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureTargetAsync(cancellationToken).ConfigureAwait(false);
            await DeletePathIfExistsAsync(ContentPath(key), cancellationToken).ConfigureAwait(false);
            await DeletePathIfExistsAsync(CurrentMetadataPath(key), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            keyLock.Release();
        }
    }

    public async Task<StorageHealth> CheckHealthAsync(CancellationToken cancellationToken = default)
    {
        string probeName = $".common-storage-health/{Guid.NewGuid():N}.txt";
        byte[] bytes = Encoding.UTF8.GetBytes("ok");
        try
        {
            await EnsureTargetAsync(cancellationToken).ConfigureAwait(false);
            await EnsureFolderPathAsync(ParentFolder(probeName), cancellationToken).ConfigureAwait(false);
            await using MemoryStream source = new(bytes, writable: false);
            _ = await PutContentAsync(probeName, source, "text/plain", cancellationToken).ConfigureAwait(false);

            HttpResponseMessage response = await SendGraphAsync(
                HttpMethod.Get,
                DrivePathContentUrl(probeName),
                null,
                cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                string detail = await SafeReadBodyAsync(response, cancellationToken).ConfigureAwait(false);
                response.Dispose();
                return new StorageHealth(true, false, nameof(SharePointFileStorage), RootDescription(), detail);
            }

            string text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            response.Dispose();
            await DeletePathIfExistsAsync(probeName, cancellationToken).ConfigureAwait(false);

            return string.Equals(text, "ok", StringComparison.Ordinal)
                ? new StorageHealth(true, true, nameof(SharePointFileStorage), RootDescription())
                : new StorageHealth(true, false, nameof(SharePointFileStorage), RootDescription(), "SharePoint health probe read-back did not match.");
        }
        catch (Exception exception)
        {
            try { await DeletePathIfExistsAsync(probeName, CancellationToken.None).ConfigureAwait(false); } catch { }
            return new StorageHealth(false, false, nameof(SharePointFileStorage), RootDescription(), exception.Message);
        }
    }

    private async Task<StoredFile?> GetMetadataCoreAsync(string key, CancellationToken cancellationToken)
    {
        await EnsureTargetAsync(cancellationToken).ConfigureAwait(false);
        return await GetJsonAsync<StoredFile>(CurrentMetadataPath(key), cancellationToken).ConfigureAwait(false);
    }

    private async Task EnsureTargetAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(driveId))
            return;

        await targetLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!string.IsNullOrWhiteSpace(driveId))
                return;

            if (!string.IsNullOrWhiteSpace(options.SiteId))
            {
                siteId = options.SiteId.Trim();
            }
            else
            {
                string hostName = options.HostName
                    ?? throw new StorageException("STORAGE-SHAREPOINT-SITE-001", "SharePoint HostName is required when SiteId is not configured.");
                siteId = options.SitePath.Trim('/') is { Length: > 0 } sitePath
                    ? await GetRequiredStringAsync(
                        $"{GraphBase}/sites/{Uri.EscapeDataString(hostName)}:/{EncodePath(sitePath)}?$select=id",
                        "id",
                        cancellationToken).ConfigureAwait(false)
                    : await GetRequiredStringAsync(
                        $"{GraphBase}/sites/{Uri.EscapeDataString(hostName)}?$select=id",
                        "id",
                        cancellationToken).ConfigureAwait(false);
            }

            if (!string.IsNullOrWhiteSpace(options.DriveId))
            {
                driveId = options.DriveId.Trim();
                return;
            }

            if (string.IsNullOrWhiteSpace(options.DriveName))
            {
                driveId = await GetRequiredStringAsync(
                    $"{GraphBase}/sites/{Uri.EscapeDataString(siteId)}/drive?$select=id",
                    "id",
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            HttpResponseMessage response = await SendGraphAsync(
                HttpMethod.Get,
                $"{GraphBase}/sites/{Uri.EscapeDataString(siteId)}/drives?$select=id,name",
                null,
                cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                string detail = await SafeReadBodyAsync(response, cancellationToken).ConfigureAwait(false);
                response.Dispose();
                throw new StorageException("STORAGE-SHAREPOINT-DRIVE-001", $"Unable to resolve SharePoint drive: {detail}");
            }

            using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            response.Dispose();
            foreach (JsonElement value in document.RootElement.GetProperty("value").EnumerateArray())
            {
                string? name = value.TryGetProperty("name", out JsonElement n) ? n.GetString() : null;
                if (!string.Equals(name, options.DriveName, StringComparison.OrdinalIgnoreCase))
                    continue;

                driveId = value.GetProperty("id").GetString();
                break;
            }

            if (string.IsNullOrWhiteSpace(driveId))
                throw new StorageException("STORAGE-SHAREPOINT-DRIVE-002", $"SharePoint drive '{options.DriveName}' was not found.");
        }
        finally
        {
            targetLock.Release();
        }
    }

    private async Task<string> GetRequiredStringAsync(string url, string property, CancellationToken cancellationToken)
    {
        HttpResponseMessage response = await SendGraphAsync(HttpMethod.Get, url, null, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            string detail = await SafeReadBodyAsync(response, cancellationToken).ConfigureAwait(false);
            response.Dispose();
            throw new StorageException("STORAGE-SHAREPOINT-RESOLVE-001", $"SharePoint target resolution failed: {detail}");
        }

        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        response.Dispose();
        string? value = document.RootElement.TryGetProperty(property, out JsonElement element)
            ? element.GetString()
            : null;
        return string.IsNullOrWhiteSpace(value)
            ? throw new StorageException("STORAGE-SHAREPOINT-RESOLVE-002", $"SharePoint response did not contain '{property}'.")
            : value;
    }

    private async Task EnsureFolderPathAsync(string relativeFolder, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(relativeFolder))
            return;

        await EnsureTargetAsync(cancellationToken).ConfigureAwait(false);
        string current = string.Empty;
        foreach (string segment in SplitPath(relativeFolder))
        {
            string parent = current;
            current = CombinePath(current, segment);
            HttpResponseMessage exists = await SendGraphAsync(
                HttpMethod.Get,
                DriveItemUrl(current),
                null,
                cancellationToken).ConfigureAwait(false);
            if (exists.IsSuccessStatusCode)
            {
                exists.Dispose();
                continue;
            }

            if (exists.StatusCode != HttpStatusCode.NotFound)
            {
                string detail = await SafeReadBodyAsync(exists, cancellationToken).ConfigureAwait(false);
                exists.Dispose();
                throw new StorageException("STORAGE-SHAREPOINT-FOLDER-001", $"Unable to inspect SharePoint folder '{current}': {detail}");
            }
            exists.Dispose();

            string body = JsonSerializer.Serialize(new
            {
                name = segment,
                folder = new { },
                @microsoft_graph_conflictBehavior = "fail"
            }).Replace("microsoft_graph_conflictBehavior", "@microsoft.graph.conflictBehavior", StringComparison.Ordinal);

            using StringContent content = new(body, Encoding.UTF8, "application/json");
            HttpResponseMessage create = await SendGraphAsync(
                HttpMethod.Post,
                DriveChildrenUrl(parent),
                content,
                cancellationToken).ConfigureAwait(false);
            if (!create.IsSuccessStatusCode && create.StatusCode != HttpStatusCode.Conflict)
            {
                string detail = await SafeReadBodyAsync(create, cancellationToken).ConfigureAwait(false);
                create.Dispose();
                throw new StorageException("STORAGE-SHAREPOINT-FOLDER-002", $"Unable to create SharePoint folder '{current}': {detail}");
            }
            create.Dispose();
        }
    }

    private async Task<GraphStoredItem> PutContentAsync(string relativePath, Stream content, string contentType, CancellationToken cancellationToken)
    {
        using StreamContent body = new(content);
        body.Headers.ContentType = MediaTypeHeaderValue.Parse(NormalizeContentType(contentType));
        HttpResponseMessage response = await SendGraphAsync(
            HttpMethod.Put,
            DrivePathContentUrl(relativePath),
            body,
            cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            string detail = await SafeReadBodyAsync(response, cancellationToken).ConfigureAwait(false);
            response.Dispose();
            throw new StorageException("STORAGE-SHAREPOINT-UPLOAD-001", $"SharePoint upload failed for '{relativePath}': {detail}");
        }

        string json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        response.Dispose();
        using JsonDocument document = JsonDocument.Parse(json);
        string itemId = document.RootElement.TryGetProperty("id", out JsonElement idElement)
            ? idElement.GetString() ?? string.Empty
            : string.Empty;
        string? eTag = document.RootElement.TryGetProperty("eTag", out JsonElement eTagElement)
            ? eTagElement.GetString()
            : null;
        if (string.IsNullOrWhiteSpace(itemId))
            throw new StorageException("STORAGE-SHAREPOINT-UPLOAD-002", "Microsoft Graph upload response did not include an item id.");
        return new GraphStoredItem(itemId, eTag);
    }

    private async Task PutJsonAsync<T>(string relativePath, T value, CancellationToken cancellationToken)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        await using MemoryStream stream = new(bytes, writable: false);
        _ = await PutContentAsync(relativePath, stream, "application/json", cancellationToken).ConfigureAwait(false);
    }

    private async Task<T?> GetJsonAsync<T>(string relativePath, CancellationToken cancellationToken)
    {
        HttpResponseMessage response = await SendGraphAsync(
            HttpMethod.Get,
            DrivePathContentUrl(relativePath),
            null,
            cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            response.Dispose();
            return default;
        }

        if (!response.IsSuccessStatusCode)
        {
            string detail = await SafeReadBodyAsync(response, cancellationToken).ConfigureAwait(false);
            response.Dispose();
            throw new StorageException("STORAGE-SHAREPOINT-METADATA-001", $"Unable to read storage metadata '{relativePath}': {detail}");
        }

        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        T? result = await JsonSerializer.DeserializeAsync<T>(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        response.Dispose();
        return result;
    }

    private async Task DeletePathIfExistsAsync(string relativePath, CancellationToken cancellationToken)
    {
        HttpResponseMessage response = await SendGraphAsync(
            HttpMethod.Delete,
            DriveItemUrl(relativePath),
            null,
            cancellationToken).ConfigureAwait(false);
        if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.NotFound)
        {
            response.Dispose();
            return;
        }

        string detail = await SafeReadBodyAsync(response, cancellationToken).ConfigureAwait(false);
        response.Dispose();
        throw new StorageException("STORAGE-SHAREPOINT-DELETE-001", $"Unable to delete '{relativePath}' from SharePoint: {detail}");
    }

    private async Task<HttpResponseMessage> SendGraphAsync(
        HttpMethod method,
        string url,
        HttpContent? content,
        CancellationToken cancellationToken)
    {
        string token = await GetAccessTokenAsync(false, cancellationToken).ConfigureAwait(false);
        HttpRequestMessage request = new(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = content;
        HttpResponseMessage response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        request.Dispose();

        if (response.StatusCode != HttpStatusCode.Unauthorized)
            return response;

        response.Dispose();
        if (content is not null)
            throw new StorageException("STORAGE-SHAREPOINT-AUTH-RETRY-001", "SharePoint authentication expired during a write; retry the storage operation.");

        token = await GetAccessTokenAsync(true, cancellationToken).ConfigureAwait(false);
        HttpRequestMessage retry = new(method, url);
        retry.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        HttpResponseMessage retried = await httpClient.SendAsync(retry, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        retry.Dispose();
        return retried;
    }

    private async Task<string> GetAccessTokenAsync(bool forceRefresh, CancellationToken cancellationToken)
    {
        if (!forceRefresh &&
            !string.IsNullOrWhiteSpace(accessToken) &&
            tokenExpiresUtc > DateTimeOffset.UtcNow.AddMinutes(2))
            return accessToken;

        await tokenLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!forceRefresh &&
                !string.IsNullOrWhiteSpace(accessToken) &&
                tokenExpiresUtc > DateTimeOffset.UtcNow.AddMinutes(2))
                return accessToken;

            using FormUrlEncodedContent body = new(new Dictionary<string, string>
            {
                ["client_id"] = options.ClientId,
                ["client_secret"] = options.ClientSecret,
                ["scope"] = "https://graph.microsoft.com/.default",
                ["grant_type"] = "client_credentials"
            });
            using HttpRequestMessage request = new(
                HttpMethod.Post,
                $"https://login.microsoftonline.com/{Uri.EscapeDataString(options.TenantId)}/oauth2/v2.0/token")
            {
                Content = body
            };
            using HttpResponseMessage response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            string json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new StorageException("STORAGE-SHAREPOINT-AUTH-001", $"Unable to acquire Microsoft Graph token: {json}");

            using JsonDocument document = JsonDocument.Parse(json);
            accessToken = document.RootElement.GetProperty("access_token").GetString()
                ?? throw new StorageException("STORAGE-SHAREPOINT-AUTH-002", "Microsoft identity response did not contain an access token.");
            int expiresIn = document.RootElement.TryGetProperty("expires_in", out JsonElement expires)
                ? expires.GetInt32()
                : 3600;
            tokenExpiresUtc = DateTimeOffset.UtcNow.AddSeconds(expiresIn);
            return accessToken;
        }
        finally
        {
            tokenLock.Release();
        }
    }

    private string ContentPath(string key) => CombinePath(options.RootFolder, key);
    private string MetadataRoot => CombinePath(options.RootFolder, ".common-storage");
    private string CurrentMetadataPath(string key) => CombinePath(MetadataRoot, "metadata", DigestKey(key) + ".json");
    private string VersionMetadataFolder(string key) => CombinePath(MetadataRoot, "versions", DigestKey(key));
    private string VersionMetadataPath(string key, int version) => CombinePath(VersionMetadataFolder(key), $"{version:D8}.json");

    private string DrivePathContentUrl(string relativePath) =>
        $"{GraphBase}/drives/{Uri.EscapeDataString(driveId!)}/root:/{EncodePath(relativePath)}:/content";

    private string DriveItemUrl(string relativePath) =>
        string.IsNullOrWhiteSpace(relativePath)
            ? $"{GraphBase}/drives/{Uri.EscapeDataString(driveId!)}/root"
            : $"{GraphBase}/drives/{Uri.EscapeDataString(driveId!)}/root:/{EncodePath(relativePath)}";

    private string DriveChildrenUrl(string relativeFolder) =>
        string.IsNullOrWhiteSpace(relativeFolder)
            ? $"{GraphBase}/drives/{Uri.EscapeDataString(driveId!)}/root/children"
            : $"{GraphBase}/drives/{Uri.EscapeDataString(driveId!)}/root:/{EncodePath(relativeFolder)}:/children";

    private string RootDescription() =>
        !string.IsNullOrWhiteSpace(options.HostName)
            ? $"https://{options.HostName}/{options.SitePath.Trim('/')}/{options.DriveName ?? options.DriveId ?? "default-drive"}/{options.RootFolder.Trim('/')}"
            : $"sharepoint://sites/{options.SiteId}/{options.DriveName ?? options.DriveId ?? "default-drive"}/{options.RootFolder.Trim('/')}";

    private static string ParentFolder(string path)
    {
        int index = path.LastIndexOf('/');
        return index <= 0 ? string.Empty : path[..index];
    }

    private static string CombinePath(params string?[] values) =>
        string.Join("/", values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!.Replace('\\', '/').Trim('/')));

    private static string[] SplitPath(string path) =>
        path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string EncodePath(string path) =>
        string.Join("/", SplitPath(path).Select(Uri.EscapeDataString));

    private static string DigestKey(string key) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();

    private static string NormalizeKey(string storageKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageKey);
        string key = storageKey.Replace('\\', '/').Trim('/');
        string[] parts = key.Split('/');
        if (parts.Any(part => string.IsNullOrWhiteSpace(part) || part is "." or ".."))
            throw new StorageException("STORAGE-PATH-002", "Storage key is invalid.");
        if (parts.Any(ContainsUnsafePathCharacters))
            throw new StorageException("STORAGE-PATH-005", "Storage key contains characters that are unsafe across supported providers.");
        return key;
    }

    private static bool ContainsUnsafePathCharacters(string part)
    {
        foreach (char value in part)
            if (char.IsControl(value) || value is ':' or '*' or '?' or '"' or '<' or '>' or '|')
                return true;
        return false;
    }

    private static string NormalizeContentType(string contentType) =>
        string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType.Trim();

    private static void ValidateOptions(SharePointStorageOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(options.TenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ClientId);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ClientSecret);
        if (string.IsNullOrWhiteSpace(options.SiteId) && string.IsNullOrWhiteSpace(options.HostName))
            throw new ArgumentException("SharePoint SiteId or HostName is required.", nameof(options));
    }

    private static async Task<(long Length, string Sha256)> CopyAndHashAsync(
        Stream source,
        string destinationPath,
        long? maximumBytes,
        CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[BufferSize];
        long total = 0;
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using FileStream destination = new(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        while (true)
        {
            int read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
            if (read == 0)
                break;

            total += read;
            if (maximumBytes.HasValue && total > maximumBytes.Value)
                throw new StorageException("STORAGE-SIZE-001", $"File exceeds configured limit of {maximumBytes.Value} bytes.");

            hash.AppendData(buffer, 0, read);
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        if (total == 0)
            throw new StorageException("STORAGE-SIZE-002", "File cannot be empty.");

        await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
        return (total, Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
    }

    private static async Task<string> ComputeHashAsync(string path, CancellationToken cancellationToken)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[BufferSize];
        await using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        while (true)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
            if (read == 0)
                break;
            hash.AppendData(buffer, 0, read);
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private sealed record GraphStoredItem(string ItemId, string? ETag);

    private static async Task<string> SafeReadBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(body)
                ? $"{(int)response.StatusCode} {response.ReasonPhrase}"
                : body;
        }
        catch
        {
            return $"{(int)response.StatusCode} {response.ReasonPhrase}";
        }
    }
}
