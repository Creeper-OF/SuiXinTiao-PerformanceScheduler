using PerformanceScheduler.Core.Models;
using PerformanceScheduler.Core.Services;

namespace PerformanceScheduler.Tests;

public sealed class DeviceStrategyRuntimeSelectorTests
{
    [Fact]
    public void Select_AddsEnabledProfileFromMatchingHardwarePackage()
    {
        var device = CreateDevice("Legion 5", "Ryzen 7", "RTX 3060");
        var package = CreatePackage("hardware-pack", device, CreateAppProfileItem("package-game"));

        var result = new DeviceStrategyRuntimeSelector().Select(
            device,
            PowerSourceMode.Ac,
            [package],
            [new PerformanceProfile { Id = "local-default", IsGlobalDefault = true }]);

        Assert.Same(package, result.SelectedPackage);
        Assert.Equal(DeviceCompatibilityLevel.SameHardware, result.SelectedCompatibility?.Level);
        Assert.Contains(result.Profiles, profile => profile.Id == "package-game");
        Assert.Contains("package-game", result.PackageProfileIds);
        Assert.Equal("DeviceStrategyRuntime.SelectedSameHardware", result.SelectionReasonKey);
    }

    [Fact]
    public void Select_PrefersSameHardwarePackageOverSameModelPackage()
    {
        var device = CreateDevice("Legion 5", "Ryzen 7", "RTX 3060");
        var sameHardware = CreatePackage(
            "same-hardware",
            device,
            CreateAppProfileItem("hardware-profile"));
        var sameModel = CreatePackage(
            "same-model",
            CreateDevice("Legion 5", "Ryzen 5", "RTX 3050"),
            CreateAppProfileItem("model-profile"));

        var result = new DeviceStrategyRuntimeSelector().Select(
            device,
            PowerSourceMode.Ac,
            [sameModel, sameHardware],
            Array.Empty<PerformanceProfile>());

        Assert.Equal("same-hardware", result.SelectedPackage?.PackageId);
        Assert.Contains(result.Profiles, profile => profile.Id == "hardware-profile");
        Assert.DoesNotContain(result.Profiles, profile => profile.Id == "model-profile");
    }

    [Fact]
    public void Select_UsesPowerSourceToChooseBetweenCompatiblePackages()
    {
        var device = CreateDevice("Legion 5", "Ryzen 7", "RTX 3060");
        var batteryPackage = CreatePackage(
            "battery-pack",
            device,
            new DeviceStrategyProfileItem
            {
                Role = DeviceStrategyProfileRole.BatteryDefault,
                Profile = CreateAppProfile("battery-profile")
            });
        var acPackage = CreatePackage(
            "ac-pack",
            device,
            new DeviceStrategyProfileItem
            {
                Role = DeviceStrategyProfileRole.PluggedInDefault,
                Profile = CreateAppProfile("ac-profile")
            });

        var batteryResult = new DeviceStrategyRuntimeSelector().Select(
            device,
            PowerSourceMode.Battery,
            [acPackage, batteryPackage],
            Array.Empty<PerformanceProfile>());
        var acResult = new DeviceStrategyRuntimeSelector().Select(
            device,
            PowerSourceMode.Ac,
            [acPackage, batteryPackage],
            Array.Empty<PerformanceProfile>());

        Assert.Equal("battery-pack", batteryResult.SelectedPackage?.PackageId);
        Assert.Equal(PowerSourceMode.Battery, Assert.Single(batteryResult.Profiles).PowerSourceMode);
        Assert.Equal("ac-pack", acResult.SelectedPackage?.PackageId);
        Assert.Equal(PowerSourceMode.Ac, Assert.Single(acResult.Profiles).PowerSourceMode);
    }

    [Fact]
    public void Select_LocalProfileWinsWhenPackageContainsTheSameId()
    {
        var device = CreateDevice("Legion 5", "Ryzen 7", "RTX 3060");
        var package = CreatePackage(
            "package",
            device,
            CreateAppProfileItem("same-id", "Package version"));
        var local = CreateAppProfile("same-id", "Local version");

        var result = new DeviceStrategyRuntimeSelector().Select(
            device,
            PowerSourceMode.Ac,
            [package],
            [local]);

        var selected = Assert.Single(result.Profiles);
        Assert.Equal("Local version", selected.Name);
        Assert.Empty(result.PackageProfileIds);
        Assert.Equal(1, result.OverlappingProfileCount);
    }

    [Fact]
    public void Select_DoesNotApplyDifferentDeviceOrDisabledProfiles()
    {
        var currentDevice = CreateDevice("Legion 5", "Ryzen 7", "RTX 3060");
        var package = CreatePackage(
            "different-device",
            CreateDevice("ROG Zephyrus", "Core i7", "RTX 4070"),
            new DeviceStrategyProfileItem
            {
                Profile = CreateAppProfile("disabled", isEnabled: false)
            });

        var result = new DeviceStrategyRuntimeSelector().Select(
            currentDevice,
            PowerSourceMode.Ac,
            [package],
            [new PerformanceProfile { Id = "local" }]);

        Assert.Null(result.SelectedPackage);
        Assert.Equal("DeviceStrategyRuntime.NoCompatiblePackage", result.SelectionReasonKey);
        Assert.Single(result.Profiles);
    }

    [Fact]
    public void Select_DoesNotApplyDisabledPackageUntilUserEnablesIt()
    {
        var device = CreateDevice("Legion 5", "Ryzen 7", "RTX 3060");
        var package = CreatePackage(
            "disabled-package",
            device,
            CreateAppProfileItem("package-profile")) with { IsEnabled = false };

        var result = new DeviceStrategyRuntimeSelector().Select(
            device,
            PowerSourceMode.Ac,
            [package],
            Array.Empty<PerformanceProfile>());

        Assert.Null(result.SelectedPackage);
        Assert.Equal("DeviceStrategyRuntime.NoEnabledPackage", result.SelectionReasonKey);
        Assert.Empty(result.Profiles);
    }

    [Fact]
    public void Select_UsesPackageVersionAsDeterministicTieBreaker()
    {
        var device = CreateDevice("Legion 5", "Ryzen 7", "RTX 3060");
        var older = CreatePackage("older", device, CreateAppProfileItem("older-profile")) with { Version = 1 };
        var newer = CreatePackage("newer", device, CreateAppProfileItem("newer-profile")) with { Version = 2 };

        var result = new DeviceStrategyRuntimeSelector().Select(
            device,
            PowerSourceMode.Ac,
            [newer, older],
            Array.Empty<PerformanceProfile>());

        Assert.Equal("newer", result.SelectedPackage?.PackageId);
    }

    private static DeviceStrategyPackage CreatePackage(
        string packageId,
        DeviceFingerprint targetDevice,
        params DeviceStrategyProfileItem[] items) =>
        new()
        {
            PackageId = packageId,
            Name = packageId,
            TargetDevice = targetDevice,
            Profiles = items
        };

    private static PerformanceProfile CreateAppProfile(
        string id,
        string? name = null,
        bool isEnabled = true) =>
        new()
        {
            Id = id,
            Name = name ?? id,
            IsEnabled = isEnabled,
            Match = new ProfileMatchCriteria { ProcessNames = ["game"] }
        };

    private static DeviceStrategyProfileItem CreateAppProfileItem(
        string id,
        string? name = null) =>
        new() { Profile = CreateAppProfile(id, name) };

    private static DeviceFingerprint CreateDevice(string model, string cpu, string gpu) =>
        new()
        {
            Manufacturer = "Lenovo",
            Model = model,
            CpuName = cpu,
            GpuNames = [gpu],
            TotalMemoryBytes = 16UL * 1024 * 1024 * 1024
        };
}
