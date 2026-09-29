using Common.Diagnostics;
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
        services.AddSingleton<IStorageLifecycle>(provider => provider.GetRequiredService<LocalFileStorage>());
        services.AddScoped<IDiagnosticCheck, CommonStorageDiagnosticCheck>();
        services.AddSingleton<IDiagnosticLevelLocalTest>(_ => new StorageRootExistsDiagnosticLevelTest(rootPath));
        services.AddSingleton<IDiagnosticLevelLocalTest>(_ => new StorageRootReadableDiagnosticLevelTest(rootPath));
        services.AddSingleton<IDiagnosticLevelLocalTest>(_ => new StorageFreeSpaceDiagnosticLevelTest(rootPath));
        services.AddSingleton<IDiagnosticLevelLocalTest, StorageRoundTripDiagnosticLevelTest>();
        services.AddHealthChecks().AddCheck<StorageHealthCheck>("common-storage");
        return services;
    }

    public static IServiceCollection AddCommonStorage(
        this IServiceCollection services,
        StorageTargetOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        return options.Kind switch
        {
            StorageTargetKind.LocalFolder => AddFileSystemTarget(
                services,
                new LocalFileStorage(options.RootPath
                    ?? throw new InvalidOperationException("LocalFolder storage requires RootPath.")),
                options.RootPath!),
            StorageTargetKind.NetworkFolder => AddFileSystemTarget(
                services,
                new NetworkFolderStorage(options.RootPath
                    ?? throw new InvalidOperationException("NetworkFolder storage requires RootPath.")),
                options.RootPath!),
            StorageTargetKind.SharePoint => AddSharePointTarget(
                services,
                new SharePointFileStorage(options.SharePoint
                    ?? throw new InvalidOperationException("SharePoint storage requires SharePoint options."))),
            _ => throw new InvalidOperationException($"Unsupported storage target '{options.Kind}'.")
        };
    }

    public static IServiceCollection AddCommonStorageProvider<TProvider>(
        this IServiceCollection services)
        where TProvider : class, IFileStorage
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<TProvider>();
        services.AddSingleton<IFileStorage>(provider => provider.GetRequiredService<TProvider>());
        if (typeof(IVersionedFileStorage).IsAssignableFrom(typeof(TProvider)))
            services.AddSingleton<IVersionedFileStorage>(provider => (IVersionedFileStorage)provider.GetRequiredService<TProvider>());
        if (typeof(IStorageMaintenance).IsAssignableFrom(typeof(TProvider)))
            services.AddSingleton<IStorageMaintenance>(provider => (IStorageMaintenance)provider.GetRequiredService<TProvider>());
        if (typeof(IStorageLifecycle).IsAssignableFrom(typeof(TProvider)))
            services.AddSingleton<IStorageLifecycle>(provider => (IStorageLifecycle)provider.GetRequiredService<TProvider>());

        AddProviderDiagnostics(services);
        return services;
    }

    private static IServiceCollection AddFileSystemTarget(
        IServiceCollection services,
        IFileStorage storage,
        string rootPath)
    {
        services.AddSingleton<IFileStorage>(storage);

        if (storage is IVersionedFileStorage versioned)
            services.AddSingleton<IVersionedFileStorage>(versioned);
        if (storage is IStorageMaintenance maintenance)
            services.AddSingleton<IStorageMaintenance>(maintenance);
        if (storage is IStorageLifecycle lifecycle)
            services.AddSingleton<IStorageLifecycle>(lifecycle);

        services.AddSingleton<IDiagnosticLevelLocalTest>(_ => new StorageRootExistsDiagnosticLevelTest(rootPath));
        services.AddSingleton<IDiagnosticLevelLocalTest>(_ => new StorageRootReadableDiagnosticLevelTest(rootPath));
        services.AddSingleton<IDiagnosticLevelLocalTest>(_ => new StorageFreeSpaceDiagnosticLevelTest(rootPath));
        AddProviderDiagnostics(services);
        return services;
    }

    private static IServiceCollection AddSharePointTarget(
        IServiceCollection services,
        SharePointFileStorage storage)
    {
        services.AddSingleton(storage);
        services.AddSingleton<IFileStorage>(storage);
        AddProviderDiagnostics(services);
        return services;
    }

    private static void AddProviderDiagnostics(IServiceCollection services)
    {
        services.AddScoped<IDiagnosticCheck, CommonStorageDiagnosticCheck>();
        services.AddSingleton<IDiagnosticLevelLocalTest, StorageRoundTripDiagnosticLevelTest>();
        services.AddHealthChecks().AddCheck<StorageHealthCheck>("common-storage");
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
