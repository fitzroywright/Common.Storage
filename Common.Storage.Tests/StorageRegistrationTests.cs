using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
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

    [Fact]
    public void AddCommonStorageMaintenance_RegistersHostedWorkerAndOptions()
    {
        ServiceCollection services = new();
        services.AddLogging();
        services.AddCommonStorage(root);
        StorageMaintenanceHostedOptions options = new(
            TimeSpan.FromMinutes(30),
            new StorageMaintenanceOptions(TimeSpan.FromHours(2), 5));
        services.AddCommonStorageMaintenance(options);

        using ServiceProvider provider = services.BuildServiceProvider();
        StorageMaintenanceHostedOptions resolved = provider.GetRequiredService<StorageMaintenanceHostedOptions>();
        IEnumerable<IHostedService> hostedServices = provider.GetServices<IHostedService>();

        Assert.Same(options, resolved);
        Assert.Contains(hostedServices, service => service is StorageMaintenanceHostedService);
    }

    [Fact]
    public void AddCommonStorageMaintenance_RejectsNonPositiveInterval()
    {
        ServiceCollection services = new();
        services.AddCommonStorage(root);
        StorageMaintenanceHostedOptions options = new(
            TimeSpan.Zero,
            StorageMaintenanceOptions.Default);

        Assert.Throws<ArgumentOutOfRangeException>(() => services.AddCommonStorageMaintenance(options));
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
