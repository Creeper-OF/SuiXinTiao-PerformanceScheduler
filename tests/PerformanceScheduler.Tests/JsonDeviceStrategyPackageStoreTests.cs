using PerformanceScheduler.Core.Models;
using PerformanceScheduler.Infrastructure.Persistence;

namespace PerformanceScheduler.Tests;

public sealed class JsonDeviceStrategyPackageStoreTests : IDisposable
{
    private readonly string _tempDirectory;

    public JsonDeviceStrategyPackageStoreTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "PerformanceSchedulerLocalDeviceStrategyTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDirectory);
    }

    [Fact]
    public async Task SavePackageAsync_PersistsAndReloadsPackage()
    {
        var store = new JsonDeviceStrategyPackageStore(_tempDirectory);
        var package = new DeviceStrategyPackage
        {
            PackageId = "pack-1",
            Name = "Legion Balanced",
            Source = CommunityProfileSource.Official,
            TargetDevice = new DeviceFingerprint
            {
                Manufacturer = "Lenovo",
                Model = "Legion",
                CpuName = "Ryzen 7",
                GpuNames = ["RTX 3060"]
            },
            Profiles =
            [
                new DeviceStrategyProfileItem
                {
                    ItemId = "game",
                    Profile = new PerformanceProfile
                    {
                        Id = "game-profile",
                        Name = "Game Profile"
                    }
                }
            ]
        };

        await store.SavePackageAsync(package);

        var packages = await store.LoadPackagesAsync();

        var savedPackage = Assert.Single(packages);
        Assert.Equal("Legion Balanced", savedPackage.Name);
        Assert.Single(savedPackage.Profiles);
        Assert.Contains("pack-1", Directory.GetFiles(_tempDirectory).Single());
    }

    [Fact]
    public async Task SavePackageAsync_ReplacesPreviousFileWhenNameChanges()
    {
        var store = new JsonDeviceStrategyPackageStore(_tempDirectory);
        await store.SavePackageAsync(new DeviceStrategyPackage
        {
            PackageId = "pack-1",
            Name = "Old Name"
        });
        await store.SavePackageAsync(new DeviceStrategyPackage
        {
            PackageId = "pack-1",
            Name = "New Name"
        });

        var packages = await store.LoadPackagesAsync();

        var savedPackage = Assert.Single(packages);
        Assert.Equal("New Name", savedPackage.Name);
        Assert.Single(Directory.GetFiles(_tempDirectory, "*.json"));
    }

    [Fact]
    public async Task LoadPackagesAsync_IgnoresCorruptPackageFiles()
    {
        await File.WriteAllTextAsync(Path.Combine(_tempDirectory, "bad.json"), "{ not json");
        var store = new JsonDeviceStrategyPackageStore(_tempDirectory);

        var packages = await store.LoadPackagesAsync();

        Assert.Empty(packages);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
    }
}
