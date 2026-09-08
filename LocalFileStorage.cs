using System.Buffers;
using System.Security.Cryptography;
using System.Text.Json;

namespace Common.Storage;

public sealed class LocalFileStorage : IFileStorage
{
    private const int BufferSize = 128 * 1024;
    private readonly string rootPath;

    public LocalFileStorage(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        this.rootPath = Path.GetFullPath(rootPath);
        Directory.CreateDirectory(this.rootPath);
    }

    public async Task<StoredFile> StoreAsync(StorageWriteRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Content);
        if (!request.Content.CanRead) throw new StorageException("STORAGE-INPUT-001", "Content stream is not readable.");

        string logicalPath = ResolveLogicalPath(request.StorageKey);
        string versionsPath = logicalPath + ".versions";
        Directory.CreateDirectory(Path.GetDirectoryName(logicalPath)!);
        Directory.CreateDirectory(versionsPath);

        int version = NextVersion(versionsPath);
        string versionPath = Path.Combine(versionsPath, $"{version:D8}.bin");
        string metadataPath = Path.Combine(versionsPath, $"{version:D8}.json");
        string temporaryPath = versionPath + ".uploading";

        try
        {
            (long length, string sha256) = await WriteAndHashAsync(request.Content, temporaryPath, request.MaximumBytes, cancellationToken);
            File.Move(temporaryPath, versionPath, false);

            StoredFile stored = new(
                NormalizeKey(request.StorageKey),
                length,
                NormalizeContentType(request.ContentType),
                Path.GetFileName(request.OriginalFileName),
                sha256,
                DateTimeOffset.UtcNow,
                string.IsNullOrWhiteSpace(request.UploadedBy) ? "Unknown" : request.UploadedBy.Trim(),
                version,
                StorageStatus.Stored,
                StorageMetadata.Freeze(request.Metadata));

            await WriteMetadataAsync(metadataPath, stored, cancellationToken);
            await ReplaceCurrentAsync(versionPath, logicalPath, cancellationToken);
            await WriteMetadataAsync(logicalPath + ".json", stored, cancellationToken);
            return stored;
        }
        catch (StorageException)
        {
            DeleteIfExists(temporaryPath);
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DeleteIfExists(temporaryPath);
            throw new StorageException("STORAGE-WRITE-001", $"Unable to store '{request.StorageKey}'.", ex);
        }
    }

    public async Task<Stream> OpenReadAsync(string storageKey, CancellationToken cancellationToken = default)
    {
        string path = ResolveLogicalPath(storageKey);
        StoredFile? metadata = await ReadMetadataAsync(path + ".json", cancellationToken);
        if (!File.Exists(path)) throw new StorageException("STORAGE-MISSING-001", $"Stored file '{storageKey}' does not exist.");

        if (metadata is not null)
        {
            string hash = await ComputeHashAsync(path, cancellationToken);
            if (!string.Equals(hash, metadata.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new StorageException("STORAGE-INTEGRITY-001", $"Stored file '{storageKey}' failed SHA-256 verification.");
        }

        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
    }

    public Task<StoredFile?> GetMetadataAsync(string storageKey, CancellationToken cancellationToken = default)
        => ReadMetadataAsync(ResolveLogicalPath(storageKey) + ".json", cancellationToken);

    public async Task<IReadOnlyList<StoredFile>> GetVersionsAsync(string storageKey, CancellationToken cancellationToken = default)
    {
        string versionsPath = ResolveLogicalPath(storageKey) + ".versions";
        if (!Directory.Exists(versionsPath)) return [];

        List<StoredFile> versions = [];
        foreach (string path in Directory.EnumerateFiles(versionsPath, "*.json").OrderByDescending(path => path, StringComparer.Ordinal))
        {
            StoredFile? item = await ReadMetadataAsync(path, cancellationToken);
            if (item is not null) versions.Add(item);
        }
        return versions;
    }

    public Task DeleteAsync(string storageKey, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string path = ResolveLogicalPath(storageKey);
        DeleteIfExists(path);
        DeleteIfExists(path + ".json");
        return Task.CompletedTask;
    }

    public async Task<StorageHealth> CheckHealthAsync(CancellationToken cancellationToken = default)
    {
        string probe = Path.Combine(rootPath, $".health-{Guid.NewGuid():N}");
        try
        {
            await File.WriteAllTextAsync(probe, "ok", cancellationToken);
            DeleteIfExists(probe);
            return new StorageHealth(true, true, nameof(LocalFileStorage), rootPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DeleteIfExists(probe);
            return new StorageHealth(Directory.Exists(rootPath), false, nameof(LocalFileStorage), rootPath, ex.Message);
        }
    }

    private string ResolveLogicalPath(string storageKey)
    {
        string relative = NormalizeKey(storageKey).Replace('/', Path.DirectorySeparatorChar);
        string fullPath = Path.GetFullPath(Path.Combine(rootPath, relative));
        string rootPrefix = rootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
            throw new StorageException("STORAGE-PATH-001", "Storage key escaped the configured storage root.");
        return fullPath;
    }

    private static string NormalizeKey(string storageKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageKey);
        string key = storageKey.Replace('\\', '/').Trim('/');
        if (string.IsNullOrWhiteSpace(key) || key.Split('/').Any(part => part is "." or ".." || string.IsNullOrWhiteSpace(part)))
            throw new StorageException("STORAGE-PATH-002", "Storage key is invalid.");
        return key;
    }

    private static string NormalizeContentType(string contentType)
        => string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType.Trim();

    private static int NextVersion(string versionsPath)
    {
        int maximum = 0;
        foreach (string path in Directory.EnumerateFiles(versionsPath, "*.json"))
            if (int.TryParse(Path.GetFileNameWithoutExtension(path), out int value)) maximum = Math.Max(maximum, value);
        return checked(maximum + 1);
    }

    private static async Task<(long Length, string Sha256)> WriteAndHashAsync(Stream source, string path, long? maximumBytes, CancellationToken cancellationToken)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            long total = 0;
            using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using FileStream destination = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
            while (true)
            {
                int read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
                if (read == 0) break;
                total += read;
                if (maximumBytes.HasValue && total > maximumBytes.Value)
                    throw new StorageException("STORAGE-SIZE-001", $"File exceeds configured limit of {maximumBytes.Value} bytes.");
                hash.AppendData(buffer, 0, read);
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
            if (total == 0) throw new StorageException("STORAGE-SIZE-002", "File cannot be empty.");
            await destination.FlushAsync(cancellationToken);
            return (total, Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task ReplaceCurrentAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken)
    {
        string temporary = destinationPath + ".replacing";
        await using (FileStream source = new(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, true))
        await using (FileStream destination = new(temporary, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, true))
        {
            await source.CopyToAsync(destination, cancellationToken);
            await destination.FlushAsync(cancellationToken);
        }
        File.Move(temporary, destinationPath, true);
    }

    private static async Task<string> ComputeHashAsync(string path, CancellationToken cancellationToken)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
            while (true)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
                if (read == 0) break;
                hash.AppendData(buffer, 0, read);
            }
            return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task WriteMetadataAsync(string path, StoredFile stored, CancellationToken cancellationToken)
    {
        string temporary = path + ".writing";
        await using FileStream stream = new(temporary, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, true);
        await JsonSerializer.SerializeAsync(stream, stored, cancellationToken: cancellationToken);
        await stream.FlushAsync(cancellationToken);
        File.Move(temporary, path, true);
    }

    private static async Task<StoredFile?> ReadMetadataAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return null;
        await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, true);
        return await JsonSerializer.DeserializeAsync<StoredFile>(stream, cancellationToken: cancellationToken);
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }
}
