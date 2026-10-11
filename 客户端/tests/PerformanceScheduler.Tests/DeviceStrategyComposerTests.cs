using PerformanceScheduler.Core.Models;
using PerformanceScheduler.Core.Services;

namespace PerformanceScheduler.Tests;

public sealed class DeviceStrategyComposerTests
{
    [Fact]
    public void AddOrReplaceProfile_AddsProfileToDeviceStrategyPackage()
    {
        var composer = new DeviceStrategyComposer();
        var package = new DeviceStrategyPackage { Name = "Pack" };
        var profile = new PerformanceProfile
        {
            Id = "browser",
            Name = "Browser"
        };

        var updated = composer.AddOrReplaceProfile(package, profile, sourceProfileEntryId: "community-browser");

        var item = Assert.Single(updated.Profiles);
        Assert.Equal("browser", item.Profile.Id);
        Assert.Equal(DeviceStrategyProfileSource.UserAdded, item.Source);
        Assert.Equal("community-browser", item.SourceProfileEntryId);
    }

    [Fact]
    public void AddOrReplaceProfile_ReplacesProfileWithSameId()
    {
        var composer = new DeviceStrategyComposer();
        var package = new DeviceStrategyPackage
        {
            Profiles = new[]
            {
                new DeviceStrategyProfileItem
                {
                    Profile = new PerformanceProfile
                    {
                        Id = "same",
                        Name = "Old"
                    }
                }
            }
        };

        var updated = composer.AddOrReplaceProfile(package, new PerformanceProfile
        {
            Id = "same",
            Name = "New"
        });

        var item = Assert.Single(updated.Profiles);
        Assert.Equal("New", item.Profile.Name);
        Assert.Equal(2, updated.Version);
    }

    [Fact]
    public void AddOrReplaceProfile_InfersBatteryRole()
    {
        var composer = new DeviceStrategyComposer();

        var updated = composer.AddOrReplaceProfile(
            new DeviceStrategyPackage(),
            new PerformanceProfile
            {
                Id = "battery",
                PowerSourceMode = PowerSourceMode.Battery
            });

        Assert.Equal(DeviceStrategyProfileRole.BatteryDefault, Assert.Single(updated.Profiles).Role);
    }

    [Fact]
    public void RemoveProfile_RemovesProfileFromDeviceStrategyPackage()
    {
        var composer = new DeviceStrategyComposer();
        var package = new DeviceStrategyPackage
        {
            Profiles = new[]
            {
                new DeviceStrategyProfileItem { Profile = new PerformanceProfile { Id = "keep" } },
                new DeviceStrategyProfileItem { Profile = new PerformanceProfile { Id = "remove" } }
            }
        };

        var updated = composer.RemoveProfile(package, "remove");

        var item = Assert.Single(updated.Profiles);
        Assert.Equal("keep", item.Profile.Id);
    }

    [Fact]
    public void CreateFromProfiles_AssignsRoleFromProfileShape()
    {
        var composer = new DeviceStrategyComposer();
        var profiles = new[]
        {
            new PerformanceProfile { Id = "global", IsGlobalDefault = true },
            new PerformanceProfile { Id = "battery", PowerSourceMode = PowerSourceMode.Battery },
            new PerformanceProfile { Id = "ac", PowerSourceMode = PowerSourceMode.Ac }
        };

        var package = composer.CreateFromProfiles("Pack", new DeviceFingerprint(), profiles);

        Assert.Contains(package.Profiles, item => item.Profile.Id == "global" && item.Role == DeviceStrategyProfileRole.GlobalDefault);
        Assert.Contains(package.Profiles, item => item.Profile.Id == "battery" && item.Role == DeviceStrategyProfileRole.BatteryDefault);
        Assert.Contains(package.Profiles, item => item.Profile.Id == "ac" && item.Role == DeviceStrategyProfileRole.PluggedInDefault);
        Assert.False(package.IsEnabled);
    }

    [Fact]
    public void CreateBlank_CreatesDisabledPackageWithEmptyProfiles()
    {
        var composer = new DeviceStrategyComposer();
        var fp = new DeviceFingerprint { Model = "TestDevice" };

        var package = composer.CreateBlank("BlankPack", fp, "AuthorA", "SummaryA");

        Assert.Equal("BlankPack", package.Name);
        Assert.Equal("AuthorA", package.Author);
        Assert.Equal("SummaryA", package.Summary);
        Assert.False(package.IsEnabled);
        Assert.Empty(package.Profiles);
        Assert.Equal("TestDevice", package.TargetDevice.Model);
    }

    [Fact]
    public void Duplicate_ClonesPackageWithNewIdAndDisabledByDefault()
    {
        var composer = new DeviceStrategyComposer();
        var original = new DeviceStrategyPackage
        {
            PackageId = "orig-id",
            Name = "Original Package",
            IsEnabled = true,
            Version = 3,
            Profiles = new[]
            {
                new DeviceStrategyProfileItem { ItemId = "p1", Profile = new PerformanceProfile { Id = "prof1" } }
            }
        };

        var duplicated = composer.Duplicate(original);

        Assert.NotEqual("orig-id", duplicated.PackageId);
        Assert.Equal("Original Package - 副本", duplicated.Name);
        Assert.False(duplicated.IsEnabled);
        Assert.Equal(1, duplicated.Version);
        Assert.Single(duplicated.Profiles);
        Assert.Equal("prof1", duplicated.Profiles[0].Profile.Id);
    }
}
