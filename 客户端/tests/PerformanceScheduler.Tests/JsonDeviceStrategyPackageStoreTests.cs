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
                        Name = "Game Profile",
                        Match = new ProfileMatchCriteria { ProcessNames = new[] { "game" } }
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
    public async Task ConcurrentReadsAndWrites_AreSerializedWithoutCorruptingStore()
    {
        var store = new JsonDeviceStrategyPackageStore(_tempDirectory);

        var operations = Enumerable.Range(0, 24)
            .Select(async index =>
            {
                await store.SavePackageAsync(new DeviceStrategyPackage
                {
                    PackageId = $"concurrent-{index}",
                    Name = $"Concurrent {index}"
                });

                return await store.LoadPackagesAsync();
            })
            .ToArray();

        await Task.WhenAll(operations);

        var packages = await store.LoadPackagesAsync();
        Assert.Equal(24, packages.Count);
        Assert.Empty(store.LoadIssues);
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
    public async Task SavePackageAsync_RestoresStoreWhenReplacementWriteFails()
    {
        var store = new JsonDeviceStrategyPackageStore(_tempDirectory);
        await store.SavePackageAsync(new DeviceStrategyPackage
        {
            PackageId = "atomic-package",
            Name = "Original"
        });

        var blockedPath = Path.Combine(_tempDirectory, "atomic-package-Replacement.json");
        Directory.CreateDirectory(blockedPath);

        await Assert.ThrowsAnyAsync<Exception>(() =>
            store.SavePackageAsync(new DeviceStrategyPackage
            {
                PackageId = "atomic-package",
                Name = "Replacement"
            }));

        var saved = Assert.Single(await store.LoadPackagesAsync());
        Assert.Equal("Original", saved.Name);
        Assert.True(Directory.Exists(blockedPath));
    }

    [Fact]
    public async Task LoadPackagesAsync_IgnoresCorruptPackageFiles()
    {
        await File.WriteAllTextAsync(Path.Combine(_tempDirectory, "bad.json"), "{ not json");
        var store = new JsonDeviceStrategyPackageStore(_tempDirectory);

        var packages = await store.LoadPackagesAsync();

        Assert.Empty(packages);
        var issue = Assert.Single(store.LoadIssues);
        Assert.Equal(Path.Combine(_tempDirectory, "bad.json"), issue.SourcePath);
        Assert.Contains("could not be loaded", issue.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SavePackageAsync_RefusesToModifyStoreWithUnreadablePackage()
    {
        var brokenPath = Path.Combine(_tempDirectory, "bad.json");
        await File.WriteAllTextAsync(brokenPath, "{ not json");
        var store = new JsonDeviceStrategyPackageStore(_tempDirectory);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            store.SavePackageAsync(new DeviceStrategyPackage
            {
                PackageId = "safe-package",
                Name = "Safe Package"
            }));

        Assert.False(File.Exists(Path.Combine(_tempDirectory, "safe-package-Safe Package.json")));
        Assert.Equal("{ not json", await File.ReadAllTextAsync(brokenPath));
    }

    [Fact]
    public async Task LoadPackagesAsync_SkipsFutureSchemaAndReportsIssue()
    {
        await File.WriteAllTextAsync(
            Path.Combine(_tempDirectory, "future.json"),
            """
            { "schemaVersion": 2, "packageId": "future", "name": "Future Package" }
            """);
        var store = new JsonDeviceStrategyPackageStore(_tempDirectory);

        var packages = await store.LoadPackagesAsync();

        Assert.Empty(packages);
        var issue = Assert.Single(store.LoadIssues);
        Assert.Contains("schema version 2", issue.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LoadPackagesAsync_ClearsPreviousIssuesWhenStoreIsMissing()
    {
        await File.WriteAllTextAsync(
            Path.Combine(_tempDirectory, "future.json"),
            """
            { "schemaVersion": 2, "packageId": "future", "name": "Future Package" }
            """
        );
        var store = new JsonDeviceStrategyPackageStore(_tempDirectory);

        await store.LoadPackagesAsync();
        Directory.Delete(_tempDirectory, recursive: true);

        var packages = await store.LoadPackagesAsync();

        Assert.Empty(packages);
        Assert.Empty(store.LoadIssues);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[null]")]
    public async Task LoadPackagesAsync_SkipsMissingProfileEntriesAndReportsIssue(string profilesJson)
    {
        await File.WriteAllTextAsync(
            Path.Combine(_tempDirectory, "missing-profile.json"),
            $$"""
            {
              "schemaVersion": 1,
              "packageId": "missing-profile",
              "name": "Missing Profile",
              "profiles": {{profilesJson}}
            }
            """);
        var store = new JsonDeviceStrategyPackageStore(_tempDirectory);

        var packages = await store.LoadPackagesAsync();

        Assert.Empty(packages);
        var issue = Assert.Single(store.LoadIssues);
        Assert.Contains("requires a profile", issue.Message, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
    }
}
