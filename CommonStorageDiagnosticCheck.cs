using Common.Diagnostics;

namespace Common.Storage;

public sealed class CommonStorageDiagnosticCheck(IFileStorage storage) : IDiagnosticCheck
{
    public string Name => "Common.Storage";

    public async Task<DiagnosticResult> RunAsync(CancellationToken cancellationToken = default)
    {
        DateTimeOffset started = DateTimeOffset.UtcNow;
        try
        {
            StorageHealth health = await storage.CheckHealthAsync(cancellationToken).ConfigureAwait(false);
            DiagnosticStatus status = !health.Available
                ? DiagnosticStatus.Unhealthy
                : !health.Writable
                    ? DiagnosticStatus.Warning
                    : DiagnosticStatus.Healthy;

            string message = !health.Available
                ? $"Storage provider {health.Provider} is unavailable."
                : !health.Writable
                    ? $"Storage provider {health.Provider} is reachable but not writable."
                    : $"Storage provider {health.Provider} is available and writable.";

            return new DiagnosticResult(
                Name,
                status,
                message,
                DateTimeOffset.UtcNow - started);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new DiagnosticResult(
                Name,
                DiagnosticStatus.Unknown,
                "Storage health could not be established.",
                DateTimeOffset.UtcNow - started,
                null);
        }
    }
}
