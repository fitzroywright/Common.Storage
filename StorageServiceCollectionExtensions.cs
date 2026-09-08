using Microsoft.Extensions.DependencyInjection;

namespace Common.Storage;

public static class StorageServiceCollectionExtensions
{
    public static IServiceCollection AddCommonStorage(this IServiceCollection services, string rootPath)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);

        services.AddSingleton<IFileStorage>(_ => new LocalFileStorage(rootPath));
        return services;
    }
}
