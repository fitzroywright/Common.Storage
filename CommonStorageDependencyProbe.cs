namespace Common.Storage;

using Common.Diagnostics;
using System.Diagnostics;

public sealed class CommonStorageDependencyProbe(
    IFileStorage storage) : IDependencyDiagnosticProbe
{
    private readonly IFileStorage storage =
        storage ?? throw new ArgumentNullException(nameof(storage));

    public string Component => "Common.Storage";
    public string Dependency => "Configured storage target";
    public DependencyDiagnosticKind Kind => DependencyDiagnosticKind.Storage;
    public EngineeringDiagnosticLevel Level => EngineeringDiagnosticLevel.Level4Analysis;

    public async Task<DependencyVerificationResult> VerifyAsync(
        CancellationToken cancellationToken = default)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        try
        {
            StorageHealth health =
                await storage.CheckHealthAsync(cancellationToken).ConfigureAwait(false);
            stopwatch.Stop();

            OperationalDiagnosticState state =
                health.Available && health.Writable
                    ? OperationalDiagnosticState.Healthy
                    : health.Available
                        ? OperationalDiagnosticState.Degraded
                        : OperationalDiagnosticState.Failed;

            return new DependencyVerificationResult(
                Component,
                string.IsNullOrWhiteSpace(health.Provider) ? Dependency : health.Provider,
                Kind,
                true,
                health.Available,
                health.Available && health.Writable,
                state,
                DateTimeOffset.UtcNow,
                stopwatch.Elapsed,
                health.Available && health.Writable
                    ? $"{health.Provider} storage is available and writable."
                    : health.Error ?? $"{health.Provider} storage verification failed.",
                $"Root={health.Root}; Writable={health.Writable}",
                health.Available ? null : "STORAGE_UNAVAILABLE");
        }
        catch (Exception exception) when (
            exception is not OperationCanceledException ||
            !cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            return new DependencyVerificationResult(
                Component,
                Dependency,
                Kind,
                true,
                false,
                false,
                OperationalDiagnosticState.Failed,
                DateTimeOffset.UtcNow,
                stopwatch.Elapsed,
                "Storage health verification failed.",
                $"FailureType={exception.GetType().Name}",
                "STORAGE_PROBE_FAILED");
        }
    }
}
