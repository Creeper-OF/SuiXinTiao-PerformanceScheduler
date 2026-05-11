using PerformanceScheduler.Infrastructure.Persistence;

namespace PerformanceScheduler.Tests;

public sealed class JsonCommunityProfileCatalogTests : IDisposable
{
    private readonly string _tempDirectory;

    public JsonCommunityProfileCatalogTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "PerformanceSchedulerCommunityTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDirectory);
    }

    [Fact]
    public async Task LoadProfilesAsync_SeedsCacheFromBundledCatalog()
    {
        var seedPath = Path.Combine(_tempDirectory, "seed.json");
        var storagePath = Path.Combine(_tempDirectory, "runtime", "community-catalog.json");
        await File.WriteAllTextAsync(seedPath, """
        [
          {
            "schemaVersion": 1,
            "entryId": "official-test",
            "name": "Official Test",
            "author": "Team",
            "summary": "Seeded entry",
            "source": "Official",
            "downloads": 99,
            "updatedAt": "2026-04-12T00:00:00+00:00",
            "profile": {
              "schemaVersion": 1,
              "id": "official-test",
              "name": "Official Test",
              "version": 1
            }
          }
        ]
        """);

        var catalog = new JsonCommunityProfileCatalog(storagePath, seedPath);

        var profiles = await catalog.LoadProfilesAsync();

        var profile = Assert.Single(profiles);
        Assert.Equal("Official Test", profile.Name);
        Assert.True(File.Exists(storagePath));
    }

    [Fact]
    public async Task LoadProfilesAsync_ReturnsEmptyWhenCachedCatalogIsCorrupt()
    {
        var seedPath = Path.Combine(_tempDirectory, "missing-seed.json");
        var storagePath = Path.Combine(_tempDirectory, "runtime", "community-catalog.json");
        Directory.CreateDirectory(Path.GetDirectoryName(storagePath)!);
        await File.WriteAllTextAsync(storagePath, "{ this is not json");

        var catalog = new JsonCommunityProfileCatalog(storagePath, seedPath);

        var profiles = await catalog.LoadProfilesAsync();

        Assert.Empty(profiles);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
    }
}
