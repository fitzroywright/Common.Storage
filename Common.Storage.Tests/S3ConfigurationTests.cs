using Microsoft.Extensions.Configuration;
using Xunit;

namespace Common.Storage.Tests;

public sealed class S3ConfigurationTests
{
    [Fact]
    public void FromConfiguration_UsesProviderNeutralS3Settings()
    {
        Dictionary<string, string?> values = new()
        {
            ["Storage:Provider"] = "S3",
            ["Storage:S3:Endpoint"] = "https://truenas.example.test:9000",
            ["Storage:S3:Region"] = "us-east-1",
            ["Storage:S3:Bucket"] = "studio-media",
            ["Storage:S3:RootFolder"] = "Aegis/Studio",
            ["Storage:S3:ForcePathStyle"] = "true",
            ["Storage:S3:RequireHttps"] = "true"
        };
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

        StorageTargetOptions target = StorageTargetOptions.FromConfiguration(
            configuration,
            sectionName: "Storage",
            s3AccessKeyId: "access-key",
            s3SecretAccessKey: "secret-key");

        Assert.Equal(StorageTargetKind.S3, target.Kind);
        Assert.NotNull(target.S3);
        Assert.Equal("studio-media", target.S3!.BucketName);
        Assert.Equal("https://truenas.example.test:9000", target.S3.Endpoint);
        Assert.Equal("Aegis/Studio", target.S3.RootFolder);
        Assert.True(target.S3.ForcePathStyle);
        Assert.True(target.S3.RequireHttps);
        Assert.Equal("access-key", target.S3.AccessKeyId);
        Assert.Equal("secret-key", target.S3.SecretAccessKey);
    }

    [Fact]
    public void FromConfiguration_SupportsNamedProfileSection()
    {
        Dictionary<string, string?> values = new()
        {
            ["Storage:Profiles:Media:Provider"] = "S3",
            ["Storage:Profiles:Media:S3:Endpoint"] = "http://truenas.local:9000",
            ["Storage:Profiles:Media:S3:Bucket"] = "media",
            ["Storage:Profiles:Media:S3:RequireHttps"] = "false"
        };
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

        StorageTargetOptions target = StorageTargetOptions.FromConfiguration(
            configuration,
            sectionName: "Storage:Profiles:Media",
            s3AccessKeyId: "access",
            s3SecretAccessKey: "secret");

        Assert.Equal(StorageTargetKind.S3, target.Kind);
        Assert.Equal("media", target.S3!.BucketName);
        Assert.False(target.S3.RequireHttps);
    }

    [Fact]
    public void FromConfiguration_RejectsHttpWhenHttpsRequired()
    {
        Dictionary<string, string?> values = new()
        {
            ["Storage:Provider"] = "S3",
            ["Storage:S3:Endpoint"] = "http://truenas.local:9000",
            ["Storage:S3:Bucket"] = "media",
            ["Storage:S3:RequireHttps"] = "true"
        };
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
            StorageTargetOptions.FromConfiguration(configuration));

        Assert.Contains("must use HTTPS", exception.Message);
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("folder/../escape")]
    [InlineData("folder:bad")]
    public void ProviderNeutralKeyValidation_RejectsUnsafeKeys(string key)
    {
        StorageException exception = Assert.Throws<StorageException>(() =>
            CloudStorageSupport.NormalizeKey(key));

        Assert.StartsWith("STORAGE-PATH-", exception.Code);
    }
}
