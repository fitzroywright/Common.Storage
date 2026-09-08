using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Common.Storage;

public sealed class StorageHealthCheck(IFileStorage storage) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        StorageHealth health = await storage.CheckHealthAsync(cancellationToken);
        if (!health.Available)
        {
            return HealthCheckResult.Unhealthy(health.Error ?? "Storage is unavailable.");
        }

        if (!health.Writable)
        {
            return HealthCheckResult.Degraded(health.Error ?? "Storage is not writable.");
        }

        return HealthCheckResult.Healthy($"{health.Provider} storage is available and writable.");
    }
}
