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

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
    }
}
