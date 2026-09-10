using Microsoft.Extensions.DependencyInjection;

namespace Common.Storage;

public static class StorageServiceCollectionExtensions
{
    public static IServiceCollection AddCommonStorage(this IServiceCollection services, string rootPath)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);

        services.AddSingleton<LocalFileStorage>(_ => new LocalFileStorage(rootPath));
        services.AddSingleton<IFileStorage>(provider => provider.GetRequiredService<LocalFileStorage>());
        services.AddSingleton<IVersionedFileStorage>(provider => provider.GetRequiredService<LocalFileStorage>());
        services.AddSingleton<IStorageMaintenance>(provider => provider.GetRequiredService<LocalFileStorage>());
        services.AddHealthChecks().AddCheck<StorageHealthCheck>("common-storage");
        return services;
    }

    public static IServiceCollection AddCommonStorageMaintenance(
        this IServiceCollection services,
        StorageMaintenanceHostedOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        StorageMaintenanceHostedOptions resolved = options ?? StorageMaintenanceHostedOptions.Default;
        if (resolved.Interval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options), "Storage maintenance interval must be positive.");

        services.AddSingleton(resolved);
        services.AddHostedService<StorageMaintenanceHostedService>();
        return services;
    }
}
