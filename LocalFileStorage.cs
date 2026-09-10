using System.Buffers;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;

namespace Common.Storage;

public sealed class LocalFileStorage : IVersionedFileStorage, IStorageMaintenance, IStorageLifecycle
{
    private const int BufferSize = 128 * 1024;
    private const int InterprocessLockRetryMilliseconds = 25;
    private readonly string rootPath;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> keyLocks = new(StringComparer.OrdinalIgnoreCase);

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

        string normalizedKey = NormalizeKey(request.StorageKey);
        SemaphoreSlim keyLock = keyLocks.GetOrAdd(normalizedKey, static _ => new SemaphoreSlim(1, 1));
        await keyLock.WaitAsync(cancellationToken);
        try
        {
            return await StoreCoreAsync(request, normalizedKey, cancellationToken);
        }
        finally
        {
            keyLock.Release();
        }
    }

    public async Task<Stream> OpenReadAsync(string storageKey, CancellationToken cancellationToken = default)
    {
        string path = ResolveLogicalPath(storageKey);
        StoredFile? metadata = await ReadMetadataAsync(path + ".json", cancellationToken);
        if (!File.Exists(path)) throw new StorageException("STORAGE-MISSING-001", $"Stored file '{storageKey}' does not exist.");
        await VerifyIntegrityAsync(path, metadata, storageKey, cancellationToken);
        return OpenSequentialRead(path);
    }

    public async Task<Stream> OpenVersionAsync(string storageKey, int version, CancellationToken cancellationToken = default)
    {
        if (version <= 0) throw new ArgumentOutOfRangeException(nameof(version));
        string versionsPath = ResolveLogicalPath(storageKey) + ".versions";
        string versionPath = Path.Combine(versionsPath, $"{version:D8}.bin");
        string metadataPath = Path.Combine(versionsPath, $"{version:D8}.json");
        StoredFile? metadata = await ReadMetadataAsync(metadataPath, cancellationToken);
        if (metadata is null || !File.Exists(versionPath))
            throw new StorageException("STORAGE-VERSION-MISSING-001", $"Version {version} of '{storageKey}' does not exist.");
        await VerifyIntegrityAsync(versionPath, metadata, storageKey, cancellationToken);
        return OpenSequentialRead(versionPath);
    }

    public async Task<StoredFile> RestoreVersionAsync(string storageKey, int version, string restoredBy, CancellationToken cancellationToken = default)
    {
        if (version <= 0) throw new ArgumentOutOfRangeException(nameof(version));
        string normalizedKey = NormalizeKey(storageKey);
        string versionsPath = ResolveLogicalPath(normalizedKey) + ".versions";
        StoredFile? sourceMetadata = await ReadMetadataAsync(Path.Combine(versionsPath, $"{version:D8}.json"), cancellationToken);
        if (sourceMetadata is null)
            throw new StorageException("STORAGE-VERSION-MISSING-001", $"Version {version} of '{storageKey}' does not exist.");

        await using Stream source = await OpenVersionAsync(normalizedKey, version, cancellationToken);
        Dictionary<string, string> metadata = new(sourceMetadata.Metadata, StringComparer.OrdinalIgnoreCase)
        {
            ["RestoredFromVersion"] = version.ToString(System.Globalization.CultureInfo.InvariantCulture)
        };
        return await StoreAsync(new StorageWriteRequest(
            normalizedKey,
            source,
            sourceMetadata.ContentType,
            sourceMetadata.OriginalFileName,
            string.IsNullOrWhiteSpace(restoredBy) ? "Unknown" : restoredBy,
            metadata), cancellationToken);
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
            cancellationToken.ThrowIfCancellationRequested();
            StoredFile? item = await ReadMetadataAsync(path, cancellationToken);
            if (item is not null) versions.Add(item);
        }
        return versions;
    }

    public async Task DeleteAsync(string storageKey, CancellationToken cancellationToken = default)
    {
        string normalizedKey = NormalizeKey(storageKey);
        SemaphoreSlim keyLock = keyLocks.GetOrAdd(normalizedKey, static _ => new SemaphoreSlim(1, 1));
        await keyLock.WaitAsync(cancellationToken);
        try
        {
            string path = ResolveLogicalPath(normalizedKey);
            string versionsPath = path + ".versions";
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            Directory.CreateDirectory(versionsPath);
            await using FileStream interprocessLock = await AcquireInterprocessLockAsync(versionsPath, cancellationToken);
            DeleteIfExists(path);
            DeleteIfExists(path + ".json");
        }
        finally
        {
            keyLock.Release();
        }
    }

    public async Task<StoragePurgeResult> PurgeAsync(string storageKey, CancellationToken cancellationToken = default)
    {
        string normalizedKey = NormalizeKey(storageKey);
        SemaphoreSlim keyLock = keyLocks.GetOrAdd(normalizedKey, static _ => new SemaphoreSlim(1, 1));
        await keyLock.WaitAsync(cancellationToken);
        try
        {
            string path = ResolveLogicalPath(normalizedKey);
            string versionsPath = path + ".versions";
            bool currentFileRemoved = File.Exists(path);
            if (!currentFileRemoved && !File.Exists(path + ".json") && !Directory.Exists(versionsPath))
            {
                return new StoragePurgeResult(false, 0, 0);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            Directory.CreateDirectory(versionsPath);
            await using FileStream interprocessLock = await AcquireInterprocessLockAsync(versionsPath, cancellationToken);

            long bytesReclaimed = DeleteAndMeasure(path) + DeleteAndMeasure(path + ".json");
            int versionsRemoved = 0;
            foreach (string metadataPath in Directory.EnumerateFiles(versionsPath, "*.json").ToArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                string binaryPath = Path.ChangeExtension(metadataPath, ".bin");
                bytesReclaimed += DeleteAndMeasure(binaryPath);
                bytesReclaimed += DeleteAndMeasure(metadataPath);
                versionsRemoved++;
            }

            foreach (string temporaryPath in Directory.EnumerateFiles(versionsPath)
                .Where(IsTemporaryFile)
                .ToArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                bytesReclaimed += DeleteAndMeasure(temporaryPath);
            }

            return new StoragePurgeResult(currentFileRemoved, versionsRemoved, bytesReclaimed);
        }
        finally
        {
            keyLock.Release();
        }
    }

    public async Task<StorageHealth> CheckHealthAsync(CancellationToken cancellationToken = default)
    {
        string probe = Path.Combine(rootPath, $".health-{Guid.NewGuid():N}");
        try
        {
            await File.WriteAllTextAsync(probe, "ok", cancellationToken);
            string readBack = await File.ReadAllTextAsync(probe, cancellationToken);
            if (!string.Equals(readBack, "ok", StringComparison.Ordinal))
            {
                return new StorageHealth(true, false, nameof(LocalFileStorage), rootPath, "Storage health probe read-back did not match the written value.");
            }
            DeleteIfExists(probe);
            return new StorageHealth(true, true, nameof(LocalFileStorage), rootPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DeleteIfExists(probe);
            return new StorageHealth(Directory.Exists(rootPath), false, nameof(LocalFileStorage), rootPath, ex.Message);
        }
        finally
        {
            DeleteIfExists(probe);
        }
    }

    public async Task<StorageMaintenanceResult> RunMaintenanceAsync(StorageMaintenanceOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.StaleTemporaryFileAge < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(options));
        if (options.RetainLatestVersions is <= 0) throw new ArgumentOutOfRangeException(nameof(options));

        int temporaryFilesRemoved = 0;
        int versionsRemoved = 0;
        long bytesReclaimed = 0;
        DateTime cutoffUtc = DateTime.UtcNow - options.StaleTemporaryFileAge;

        foreach (string path in Directory.EnumerateFiles(rootPath, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsTemporaryFile(path) || File.GetLastWriteTimeUtc(path) > cutoffUtc) continue;
            long removed = TryDeleteInactiveFile(path);
            if (removed < 0) continue;
            bytesReclaimed += removed;
            temporaryFilesRemoved++;
        }

        if (options.RetainLatestVersions.HasValue)
        {
            foreach (string versionsPath in Directory.EnumerateDirectories(rootPath, "*.versions", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                await using FileStream interprocessLock = await AcquireInterprocessLockAsync(versionsPath, cancellationToken);
                List<string> metadataFiles = Directory.EnumerateFiles(versionsPath, "*.json")
                    .OrderByDescending(path => path, StringComparer.Ordinal)
                    .Skip(options.RetainLatestVersions.Value)
                    .ToList();
                foreach (string metadataPath in metadataFiles)
                {
                    string binaryPath = Path.ChangeExtension(metadataPath, ".bin");
                    bytesReclaimed += DeleteAndMeasure(binaryPath);
                    bytesReclaimed += DeleteAndMeasure(metadataPath);
                    versionsRemoved++;
                }
            }
        }

        return new StorageMaintenanceResult(temporaryFilesRemoved, versionsRemoved, bytesReclaimed);
    }

    private async Task<StoredFile> StoreCoreAsync(StorageWriteRequest request, string normalizedKey, CancellationToken cancellationToken)
    {
        string logicalPath = ResolveLogicalPath(normalizedKey);
        string versionsPath = logicalPath + ".versions";
        Directory.CreateDirectory(Path.GetDirectoryName(logicalPath)!);
        EnsureNoReparsePoints(Path.GetDirectoryName(logicalPath)!);
        Directory.CreateDirectory(versionsPath);
        EnsureNoReparsePoints(versionsPath);

        await using FileStream interprocessLock = await AcquireInterprocessLockAsync(versionsPath, cancellationToken);
        int version = NextVersion(versionsPath);
        string versionPath = Path.Combine(versionsPath, $"{version:D8}.bin");
        string metadataPath = Path.Combine(versionsPath, $"{version:D8}.json");
        string temporaryPath = versionPath + ".uploading";
        bool versionCommitted = false;

        try
        {
            (long length, string sha256) = await WriteAndHashAsync(request.Content, temporaryPath, request.MaximumBytes, cancellationToken);
            File.Move(temporaryPath, versionPath, false);

            StoredFile stored = new(
                normalizedKey,
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
            versionCommitted = true;
            await ReplaceCurrentAsync(versionPath, logicalPath, cancellationToken);
            await WriteMetadataAsync(logicalPath + ".json", stored, cancellationToken);
            return stored;
        }
        catch (StorageException)
        {
            DeleteIfExists(temporaryPath);
            if (!versionCommitted)
            {
                DeleteIfExists(versionPath);
                DeleteIfExists(metadataPath);
            }
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DeleteIfExists(temporaryPath);
            if (!versionCommitted)
            {
                DeleteIfExists(versionPath);
                DeleteIfExists(metadataPath);
            }
            throw new StorageException("STORAGE-WRITE-001", $"Unable to store '{request.StorageKey}'.", ex);
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

    private void EnsureNoReparsePoints(string path)
    {
        string current = Path.GetFullPath(path);
        string rootPrefix = rootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!current.Equals(rootPath, StringComparison.OrdinalIgnoreCase) && !current.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
            throw new StorageException("STORAGE-PATH-003", "Storage path is outside the configured root.");

        while (!current.Equals(rootPath, StringComparison.OrdinalIgnoreCase))
        {
            if (Directory.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new StorageException("STORAGE-PATH-004", "Symbolic links and reparse points are not permitted inside the storage root.");
            current = Path.GetDirectoryName(current) ?? rootPath;
        }
    }

    private static string NormalizeKey(string storageKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageKey);
        string key = storageKey.Replace('\\', '/').Trim('/');
        string[] parts = key.Split('/');
        if (string.IsNullOrWhiteSpace(key) || parts.Any(part => part is "." or ".." || string.IsNullOrWhiteSpace(part)))
            throw new StorageException("STORAGE-PATH-002", "Storage key is invalid.");
        if (parts.Any(ContainsUnsafePathCharacters))
            throw new StorageException("STORAGE-PATH-005", "Storage key contains characters that are unsafe across supported filesystems.");
        return key;
    }

    private static bool ContainsUnsafePathCharacters(string part)
    {
        foreach (char value in part)
        {
            if (char.IsControl(value) || value is ':' or '*' or '?' or '"' or '<' or '>' or '|') return true;
        }
        return false;
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

    private static async Task<FileStream> AcquireInterprocessLockAsync(string versionsPath, CancellationToken cancellationToken)
    {
        string lockPath = Path.Combine(versionsPath, ".common-storage.lock");
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    1,
                    FileOptions.Asynchronous | FileOptions.WriteThrough);
            }
            catch (IOException)
            {
                await Task.Delay(InterprocessLockRetryMilliseconds, cancellationToken);
            }
        }
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
        try
        {
            await using (FileStream source = new(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, true))
            await using (FileStream destination = new(temporary, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, true))
            {
                await source.CopyToAsync(destination, cancellationToken);
                await destination.FlushAsync(cancellationToken);
            }
            File.Move(temporary, destinationPath, true);
        }
        finally
        {
            DeleteIfExists(temporary);
        }
    }

    private static async Task VerifyIntegrityAsync(string path, StoredFile? metadata, string storageKey, CancellationToken cancellationToken)
    {
        if (metadata is null) return;
        string hash = await ComputeHashAsync(path, cancellationToken);
        if (!string.Equals(hash, metadata.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new StorageException("STORAGE-INTEGRITY-001", $"Stored file '{storageKey}' failed SHA-256 verification.");
    }

    private static FileStream OpenSequentialRead(string path)
        => new(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);

    private static async Task<string> ComputeHashAsync(string path, CancellationToken cancellationToken)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            await using FileStream stream = OpenSequentialRead(path);
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
        try
        {
            await using FileStream stream = new(temporary, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, true);
            await JsonSerializer.SerializeAsync(stream, stored, cancellationToken: cancellationToken);
            await stream.FlushAsync(cancellationToken);
            File.Move(temporary, path, true);
        }
        finally
        {
            DeleteIfExists(temporary);
        }
    }

    private static async Task<StoredFile?> ReadMetadataAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return null;
        await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, true);
        return await JsonSerializer.DeserializeAsync<StoredFile>(stream, cancellationToken: cancellationToken);
    }

    private static bool IsTemporaryFile(string path)
        => path.EndsWith(".uploading", StringComparison.OrdinalIgnoreCase)
           || path.EndsWith(".writing", StringComparison.OrdinalIgnoreCase)
           || path.EndsWith(".replacing", StringComparison.OrdinalIgnoreCase);

    private static long TryDeleteInactiveFile(string path)
    {
        if (!File.Exists(path)) return 0;
        try
        {
            long length;
            using (FileStream stream = new(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                length = stream.Length;
            }
            File.Delete(path);
            return length;
        }
        catch (IOException)
        {
            return -1;
        }
        catch (UnauthorizedAccessException)
        {
            return -1;
        }
    }

    private static long DeleteAndMeasure(string path)
    {
        if (!File.Exists(path)) return 0;
        long length = new FileInfo(path).Length;
        File.Delete(path);
        return length;
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }
}
