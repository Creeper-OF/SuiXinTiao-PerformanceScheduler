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
    }
}
