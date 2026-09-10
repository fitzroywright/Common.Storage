using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Common.Storage;

public sealed record StorageMaintenanceHostedOptions(
    TimeSpan Interval,
    StorageMaintenanceOptions Maintenance)
{
    public static StorageMaintenanceHostedOptions Default { get; } = new(
        TimeSpan.FromHours(6),
        StorageMaintenanceOptions.Default);
}

public sealed class StorageMaintenanceHostedService : BackgroundService
{
    private readonly IStorageMaintenance maintenance;
    private readonly StorageMaintenanceHostedOptions options;
    private readonly ILogger<StorageMaintenanceHostedService> logger;

    public StorageMaintenanceHostedService(
        IStorageMaintenance maintenance,
        StorageMaintenanceHostedOptions options,
        ILogger<StorageMaintenanceHostedService> logger)
    {
        this.maintenance = maintenance ?? throw new ArgumentNullException(nameof(maintenance));
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));

        if (options.Interval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options), "Storage maintenance interval must be positive.");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RunOnceAsync(stoppingToken);

        using PeriodicTimer timer = new(options.Interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await RunOnceAsync(stoppingToken);
        }
    }

    private async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            StorageMaintenanceResult result = await maintenance.RunMaintenanceAsync(options.Maintenance, cancellationToken);
            logger.LogInformation(
                "Common.Storage maintenance completed. TemporaryFilesRemoved={TemporaryFilesRemoved}; VersionsRemoved={VersionsRemoved}; BytesReclaimed={BytesReclaimed}.",
                result.TemporaryFilesRemoved,
                result.VersionsRemoved,
                result.BytesReclaimed);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Common.Storage maintenance failed.");
        }
    }
}
