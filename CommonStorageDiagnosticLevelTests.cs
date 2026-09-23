using Common.Diagnostics;

namespace Common.Storage;

public sealed class StorageRootExistsDiagnosticLevelTest(string rootPath) : IDiagnosticLevelLocalTest
{
    private readonly string rootPath = Path.GetFullPath(string.IsNullOrWhiteSpace(rootPath) ? throw new ArgumentException("Root path is required.", nameof(rootPath)) : rootPath);
    public string TestId => "COMMON.STORAGE.L5.ROOT.EXISTS";
    public string Name => "Storage root exists";
    public string Owner => "Common.Storage";
    public EngineeringDiagnosticLevel Level => EngineeringDiagnosticLevel.Level5Scan;
    public bool IsDestructive => false;

    public Task<EngineeringDiagnosticCheckResult> RunAsync(CancellationToken cancellationToken)
        => Task.FromResult(Directory.Exists(rootPath)
            ? EngineeringDiagnosticPolicy.Passed(TestId, Name, "Storage root exists.", $"Root={rootPath}")
            : EngineeringDiagnosticPolicy.Failed(TestId, Name, "Storage root does not exist.", $"Root={rootPath}"));
}

public sealed class StorageRootReadableDiagnosticLevelTest(string rootPath) : IDiagnosticLevelLocalTest
{
    private readonly string rootPath = Path.GetFullPath(string.IsNullOrWhiteSpace(rootPath) ? throw new ArgumentException("Root path is required.", nameof(rootPath)) : rootPath);
    public string TestId => "COMMON.STORAGE.L4.ROOT.READABLE";
    public string Name => "Storage root readable";
    public string Owner => "Common.Storage";
    public EngineeringDiagnosticLevel Level => EngineeringDiagnosticLevel.Level4Analysis;
    public bool IsDestructive => false;

    public Task<EngineeringDiagnosticCheckResult> RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = Directory.EnumerateFileSystemEntries(rootPath).Take(1).ToArray();
            return Task.FromResult(EngineeringDiagnosticPolicy.Passed(TestId, Name, "Storage root can be enumerated.", $"Root={rootPath}"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            return Task.FromResult(EngineeringDiagnosticPolicy.Failed(TestId, Name, "Storage root cannot be read.", $"FailureType={ex.GetType().Name}"));
        }
    }
}

public sealed class StorageFreeSpaceDiagnosticLevelTest(string rootPath) : IDiagnosticLevelLocalTest
{
    private readonly string rootPath = Path.GetFullPath(string.IsNullOrWhiteSpace(rootPath) ? throw new ArgumentException("Root path is required.", nameof(rootPath)) : rootPath);
    public string TestId => "COMMON.STORAGE.L4.RESOURCES.FREE_SPACE";
    public string Name => "Storage free space available";
    public string Owner => "Common.Storage";
    public EngineeringDiagnosticLevel Level => EngineeringDiagnosticLevel.Level4Analysis;
    public bool IsDestructive => false;

    public Task<EngineeringDiagnosticCheckResult> RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? root = Path.GetPathRoot(rootPath);
            if (string.IsNullOrWhiteSpace(root))
                return Task.FromResult(EngineeringDiagnosticPolicy.Warning(TestId, Name, "Storage volume could not be identified."));

            DriveInfo drive = new(root);
            long free = drive.AvailableFreeSpace;
            long total = drive.TotalSize;
            double percent = total <= 0 ? 0 : (double)free / total * 100d;
            string evidence = $"FreeBytes={free}; TotalBytes={total}; FreePercent={percent:F1}";

            if (free < 512L * 1024 * 1024 || percent < 2)
                return Task.FromResult(EngineeringDiagnosticPolicy.Failed(TestId, Name, "Storage volume is critically low on free space.", evidence));
            if (free < 2L * 1024 * 1024 * 1024 || percent < 10)
                return Task.FromResult(EngineeringDiagnosticPolicy.Warning(TestId, Name, "Storage volume is low on free space.", evidence));
            return Task.FromResult(EngineeringDiagnosticPolicy.Passed(TestId, Name, "Storage volume has adequate free space.", evidence));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return Task.FromResult(EngineeringDiagnosticPolicy.Warning(TestId, Name, "Storage free space could not be established.", $"FailureType={ex.GetType().Name}"));
        }
    }
}

public sealed class StorageRoundTripDiagnosticLevelTest(IFileStorage storage) : IDiagnosticLevelLocalTest
{
    private readonly IFileStorage storage = storage ?? throw new ArgumentNullException(nameof(storage));
    public string TestId => "COMMON.STORAGE.L3.ROUNDTRIP";
    public string Name => "Storage diagnostic write/read/delete round trip";
    public string Owner => "Common.Storage";
    public EngineeringDiagnosticLevel Level => EngineeringDiagnosticLevel.Level3Verification;
    public bool IsDestructive => true;

    public async Task<EngineeringDiagnosticCheckResult> RunAsync(CancellationToken cancellationToken)
    {
        string key = $"diagnostics/diagnostic-level/{Guid.NewGuid():N}.txt";
        byte[] payload = System.Text.Encoding.UTF8.GetBytes("Aegis DiagnosticLevel storage probe");
        try
        {
            await using MemoryStream input = new(payload, writable: false);
            StoredFile stored = await storage.StoreAsync(
                new StorageWriteRequest(key, input, "text/plain", "diagnostic-level.txt", "DiagnosticLevel"),
                cancellationToken).ConfigureAwait(false);

            await using Stream read = await storage.OpenReadAsync(key, cancellationToken).ConfigureAwait(false);
            using MemoryStream copy = new();
            await read.CopyToAsync(copy, cancellationToken).ConfigureAwait(false);
            bool same = copy.ToArray().SequenceEqual(payload);

            if (!same)
                return EngineeringDiagnosticPolicy.Failed(TestId, Name, "Storage round-trip content did not match.", $"Key={key}; Version={stored.Version}");

            return EngineeringDiagnosticPolicy.Passed(TestId, Name, "Storage write/read round trip succeeded.", $"Version={stored.Version}; Bytes={stored.Length}");
        }
        catch (Exception ex) when (ex is StorageException or IOException or UnauthorizedAccessException)
        {
            return EngineeringDiagnosticPolicy.Failed(TestId, Name, "Storage write/read round trip failed.", $"FailureType={ex.GetType().Name}");
        }
        finally
        {
            try { await storage.DeleteAsync(key, CancellationToken.None).ConfigureAwait(false); }
            catch { }
        }
    }
}
