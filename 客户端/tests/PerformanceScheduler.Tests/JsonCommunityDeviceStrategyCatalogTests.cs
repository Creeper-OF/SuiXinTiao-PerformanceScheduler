using PerformanceScheduler.Core.Models;
using PerformanceScheduler.Infrastructure.Persistence;

namespace PerformanceScheduler.Tests;

public sealed class JsonCommunityDeviceStrategyCatalogTests : IDisposable
{
    private readonly string _tempDirectory;

    public JsonCommunityDeviceStrategyCatalogTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "PerformanceSchedulerDeviceStrategyTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDirectory);
    }

    [Fact]
    public async Task LoadStrategiesAsync_SeedsCacheFromBundledCatalog()
    {
        var seedPath = Path.Combine(_tempDirectory, "seed-device-strategies.json");
        var storagePath = Path.Combine(_tempDirectory, "runtime", "device-strategies.json");
        await File.WriteAllTextAsync(seedPath, """
        [
          {
            "schemaVersion": 1,
            "entryId": "official-device-pack",
            "name": "Official Device Pack",
            "author": "Team",
            "summary": "Seeded device strategy",
            "source": "Official",
            "downloads": 10,
            "updatedAt": "2026-04-26T00:00:00+00:00",
            "package": {
              "schemaVersion": 1,
              "packageId": "device-pack",
              "name": "Device Pack",
              "targetDevice": {
                "manufacturer": "Lenovo",
                "model": "Legion",
                "cpuName": "Ryzen 7",
                "gpuNames": [ "RTX 3060" ]
              },
              "profiles": [
                {
                  "itemId": "game",
                  "role": "PluggedInDefault",
                  "source": "Bundled",
                  "profile": {
                    "schemaVersion": 1,
                    "id": "game-profile",
                    "name": "Game Profile"
                  }
                }
              ]
            }
          }
        ]
        """);

        var catalog = new JsonCommunityDeviceStrategyCatalog(storagePath, seedPath);

        var strategies = await catalog.LoadStrategiesAsync();

        var strategy = Assert.Single(strategies);
        Assert.Equal("Official Device Pack", strategy.Name);
        Assert.Equal("Device Pack", strategy.Package.Name);
        Assert.Single(strategy.Package.Profiles);
        Assert.True(File.Exists(storagePath));
    }

    [Fact]
    public async Task LoadStrategiesAsync_ReturnsEmptyWhenCachedCatalogIsCorrupt()
    {
        var seedPath = Path.Combine(_tempDirectory, "missing-seed.json");
        var storagePath = Path.Combine(_tempDirectory, "runtime", "device-strategies.json");
        Directory.CreateDirectory(Path.GetDirectoryName(storagePath)!);
        await File.WriteAllTextAsync(storagePath, "{ this is not json");

        var catalog = new JsonCommunityDeviceStrategyCatalog(storagePath, seedPath);

        var strategies = await catalog.LoadStrategiesAsync();

        Assert.Empty(strategies);
    }

    [Fact]
    public async Task LoadStrategiesAsync_SkipsFutureNestedPackageSchemaAndReportsIssue()
    {
        var seedPath = Path.Combine(_tempDirectory, "missing-seed.json");
        var storagePath = Path.Combine(_tempDirectory, "runtime", "device-strategies.json");
        Directory.CreateDirectory(Path.GetDirectoryName(storagePath)!);
        await File.WriteAllTextAsync(storagePath, """
        [
          {
            "schemaVersion": 1,
            "entryId": "future",
            "package": { "schemaVersion": 2, "packageId": "future" }
          }
        ]
        """);
        var catalog = new JsonCommunityDeviceStrategyCatalog(storagePath, seedPath);

        var strategies = await catalog.LoadStrategiesAsync();

        Assert.Empty(strategies);
        Assert.Single(catalog.LoadIssues);
    }

    [Fact]
    public async Task LoadStrategiesAsync_SkipsNullEntryAndKeepsCatalogUsable()
    {
        var seedPath = Path.Combine(_tempDirectory, "missing-seed.json");
        var storagePath = Path.Combine(_tempDirectory, "runtime", "device-strategies.json");
        Directory.CreateDirectory(Path.GetDirectoryName(storagePath)!);
        await File.WriteAllTextAsync(storagePath, """
        [
          null,
          {
            "schemaVersion": 1,
            "entryId": "valid-entry",
            "name": "Valid Entry",
            "package": {
              "schemaVersion": 1,
              "packageId": "valid-package",
              "name": "Valid Package",
              "profiles": []
            }
          }
        ]
        """);

        var catalog = new JsonCommunityDeviceStrategyCatalog(storagePath, seedPath);

        var strategies = await catalog.LoadStrategiesAsync();

        Assert.Equal("Valid Entry", Assert.Single(strategies).Name);
        Assert.Single(catalog.LoadIssues);
    }

    [Fact]
    public async Task BundledCatalog_UsesCurrentCpuAndGpuSettingFields()
    {
        var repositoryRoot = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var seedPath = Path.Combine(repositoryRoot, "community", "sample-device-strategies.json");
        var storagePath = Path.Combine(_tempDirectory, "runtime", "device-strategies.json");
        var catalog = new JsonCommunityDeviceStrategyCatalog(storagePath, seedPath);

        var strategies = await catalog.LoadStrategiesAsync();

        var strategy = Assert.Single(strategies);
        Assert.Equal(95, strategy.Package.Profiles[0].Profile.PowerPlan.Advanced.ProcessorMaxStatePercent);
        Assert.Equal(75, strategy.Package.Profiles[1].Profile.PowerPlan.Advanced.ProcessorMaxStatePercent);
        Assert.Equal(1200, strategy.Package.Profiles[1].Profile.Gpu.MaxCoreClockMHz);
    }

    [Fact]
    public async Task SaveStrategiesAsync_PersistsValidStrategies()
    {
        var seedPath = Path.Combine(_tempDirectory, "missing-seed.json");
        var storagePath = Path.Combine(_tempDirectory, "runtime", "device-strategies.json");
        var catalog = new JsonCommunityDeviceStrategyCatalog(storagePath, seedPath);

        var entriesToSave = new[]
        {
            new CommunityDeviceStrategyEntry
            {
                SchemaVersion = 1,
                EntryId = "strat-save-1",
                Name = "Saved Strategy 1",
                Package = new DeviceStrategyPackage
                {
                    SchemaVersion = 1,
                    PackageId = "strat-pkg-1",
                    Name = "Saved Strategy Package",
                    Profiles = []
                }
            }
        };

        await catalog.SaveStrategiesAsync(entriesToSave);

        var reloaded = await catalog.LoadStrategiesAsync();
        var item = Assert.Single(reloaded);
        Assert.Equal("strat-save-1", item.EntryId);
        Assert.Equal("Saved Strategy 1", item.Name);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
    }
}
