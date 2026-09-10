using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Xunit;

namespace Common.Storage.Tests;

public sealed class StorageRegistrationTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "common-storage-registration-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void AddCommonStorage_RegistersOneSharedProviderForAllStorageContracts()
    {
        ServiceCollection services = new();
        services.AddCommonStorage(root);

        using ServiceProvider provider = services.BuildServiceProvider();
        IFileStorage fileStorage = provider.GetRequiredService<IFileStorage>();
        IVersionedFileStorage versioned = provider.GetRequiredService<IVersionedFileStorage>();
        IStorageMaintenance maintenance = provider.GetRequiredService<IStorageMaintenance>();

        Assert.Same(fileStorage, versioned);
        Assert.Same(fileStorage, maintenance);
        Assert.IsType<LocalFileStorage>(fileStorage);
    }

    [Fact]
    public async Task AddCommonStorage_RegistersHealthyAspNetHealthCheck()
    {
        ServiceCollection services = new();
        services.AddCommonStorage(root);

        using ServiceProvider provider = services.BuildServiceProvider();
        HealthCheckService healthChecks = provider.GetRequiredService<HealthCheckService>();
        HealthReport report = await healthChecks.CheckHealthAsync();

        Assert.Equal(HealthStatus.Healthy, report.Status);
        Assert.True(report.Entries.ContainsKey("common-storage"));
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
