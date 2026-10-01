using System.Security.Cryptography;
using System.Text.Json;

namespace Common.Storage;

internal static class CloudStorageSupport
{
    public static string NormalizeKey(string storageKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageKey);
        string key = storageKey.Replace('\\', '/').Trim('/');
        string[] parts = key.Split('/');
        if (string.IsNullOrWhiteSpace(key) || parts.Any(part => part is "." or ".." || string.IsNullOrWhiteSpace(part)))
            throw new StorageException("STORAGE-PATH-002", "Storage key is invalid.");
        if (parts.Any(part => part.Any(ch => char.IsControl(ch) || ch is ':' or '*' or '?' or '"' or '<' or '>' or '|')))
            throw new StorageException("STORAGE-PATH-005", "Storage key contains characters that are unsafe across supported storage targets.");
        return key;
    }

    public static async Task<(byte[] Bytes, long Length, string Sha256)> ReadAndHashAsync(
        Stream source,
        long? maximumBytes,
        CancellationToken cancellationToken)
    {
        using MemoryStream output = new();
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[128 * 1024];
        long total = 0;
        while (true)
        {
            int read = await source.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (read == 0) break;
            total += read;
            if (maximumBytes.HasValue && total > maximumBytes.Value)
                throw new StorageException("STORAGE-SIZE-001", $"File exceeds configured limit of {maximumBytes.Value} bytes.");
            hash.AppendData(buffer, 0, read);
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
        if (total == 0) throw new StorageException("STORAGE-SIZE-002", "File cannot be empty.");
        return (output.ToArray(), total, Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
    }

    public static string Prefix(string rootFolder, string key)
    {
        string root = string.IsNullOrWhiteSpace(rootFolder) ? string.Empty : NormalizeKey(rootFolder) + "/";
        return root + NormalizeKey(key);
    }

    public static string MetadataKey(string rootFolder, string key) =>
        Prefix(rootFolder, "__commonstorage/metadata/" + NormalizeKey(key) + ".json");

    public static string VersionMetadataPrefix(string rootFolder, string key) =>
        Prefix(rootFolder, "__commonstorage/versions/" + NormalizeKey(key) + "/");

    public static string VersionMetadataKey(string rootFolder, string key, int version) =>
        VersionMetadataPrefix(rootFolder, key) + $"{version:D8}.json";

    public static string Serialize(StoredFile value) =>
        JsonSerializer.Serialize(value);

    public static StoredFile? Deserialize(string json) =>
        JsonSerializer.Deserialize<StoredFile>(json);

    public static int NextVersion(StoredFile? current) =>
        checked((current?.Version ?? 0) + 1);
}
