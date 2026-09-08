using System.Collections.ObjectModel;

namespace Common.Storage;

public enum StorageStatus
{
    Stored,
    Missing,
    Corrupt,
    Unavailable,
    Deleted
}

public sealed record StorageWriteRequest(
    string StorageKey,
    Stream Content,
    string ContentType,
    string OriginalFileName,
    string UploadedBy,
    IReadOnlyDictionary<string, string>? Metadata = null,
    long? MaximumBytes = null);

public sealed record StoredFile(
    string StorageKey,
    long Length,
    string ContentType,
    string OriginalFileName,
    string Sha256,
    DateTimeOffset StoredUtc,
    string UploadedBy,
    int Version,
    StorageStatus Status,
    IReadOnlyDictionary<string, string> Metadata);

public sealed record StorageHealth(
    bool Available,
    bool Writable,
    string Provider,
    string Root,
    string? Error = null);

public interface IFileStorage
{
    Task<StoredFile> StoreAsync(StorageWriteRequest request, CancellationToken cancellationToken = default);
    Task<Stream> OpenReadAsync(string storageKey, CancellationToken cancellationToken = default);
    Task<StoredFile?> GetMetadataAsync(string storageKey, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<StoredFile>> GetVersionsAsync(string storageKey, CancellationToken cancellationToken = default);
    Task DeleteAsync(string storageKey, CancellationToken cancellationToken = default);
    Task<StorageHealth> CheckHealthAsync(CancellationToken cancellationToken = default);
}

public sealed class StorageException : IOException
{
    public StorageException(string code, string message, Exception? innerException = null)
        : base($"{code}: {message}", innerException)
    {
        Code = code;
    }

    public string Code { get; }
}

internal static class StorageMetadata
{
    public static IReadOnlyDictionary<string, string> Freeze(IReadOnlyDictionary<string, string>? metadata)
        => new ReadOnlyDictionary<string, string>(
            metadata is null
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(metadata, StringComparer.OrdinalIgnoreCase));
}
