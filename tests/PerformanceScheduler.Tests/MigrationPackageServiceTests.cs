using PerformanceScheduler.App.Services;
using PerformanceScheduler.App.Settings;
using PerformanceScheduler.Core.Abstractions;
using PerformanceScheduler.Core.Models;
using PerformanceScheduler.Infrastructure.Persistence;

namespace PerformanceScheduler.Tests;

public sealed class MigrationPackageServiceTests : IDisposable
{
    private readonly string _tempDirectory;

    public MigrationPackageServiceTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "PerformanceScheduler.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDirectory);
    }

    [Fact]
    public async Task ExportAsync_WritesProfilesSettingsAndDeviceFingerprint()
    {
        var service = CreateService("source", CreateDevice("Contoso", "FalconBook", "Falcon CPU", "Falcon GPU"));
        await service.ProfileManager.SaveProfileAsync(new PerformanceProfile
        {
            Id = "game-profile",
            Name = "Game Profile",
            Notes = "export me"
        });
        await service.DeviceStrategyPackageStore.SavePackageAsync(new DeviceStrategyPackage
        {
            PackageId = "device-pack",
            Name = "Device Pack",
            Profiles =
            [
                new DeviceStrategyProfileItem
                {
                    Profile = new PerformanceProfile
                    {
                        Id = "game-profile",
                        Name = "Game Profile"
                    }
                }
            ]
        });
        service.SettingsStore.Update(settings =>
        {
            settings.LanguageCode = "zh-CN";
            settings.MonitoringIntervalSeconds = 5;
        });
        var packagePath = Path.Combine(_tempDirectory, "migration.sxtpkg.json");

        await service.PackageService.ExportAsync(packagePath, new MigrationPackageExportOptions());

        var preview = await service.PackageService.PreviewImportAsync(packagePath);
        Assert.True(File.Exists(packagePath));
        Assert.Equal(1, preview.ProfileCount);
        Assert.Equal(1, preview.DeviceStrategyPackageCount);
        Assert.True(preview.HasSettings);
        Assert.Equal(DeviceCompatibilityLevel.SameHardware, preview.Compatibility.Level);
    }

    [Fact]
    public async Task ImportAsync_RespectsOverwriteOptionAndImportsSettings()
    {
        var source = CreateService("source", CreateDevice("Contoso", "FalconBook", "Falcon CPU", "Falcon GPU"));
        await source.ProfileManager.SaveProfileAsync(new PerformanceProfile
        {
            Id = "same-id",
            Name = "Imported Existing",
            Notes = "imported version"
        });
        await source.ProfileManager.SaveProfileAsync(new PerformanceProfile
        {
            Id = "new-id",
            Name = "Imported New",
            Notes = "new profile"
        });
        await source.DeviceStrategyPackageStore.SavePackageAsync(new DeviceStrategyPackage
        {
            PackageId = "device-pack",
            Name = "Imported Device Pack",
            Profiles =
            [
                new DeviceStrategyProfileItem
                {
                    Profile = new PerformanceProfile
                    {
                        Id = "new-id",
                        Name = "Imported New"
                    }
                }
            ]
        });
        source.SettingsStore.Update(settings =>
        {
            settings.LanguageCode = "zh-CN";
            settings.UnsafeRunThreshold = 4;
            settings.AutoStartMonitoringOnLaunch = true;
            settings.LaunchAtStartup = true;
        });
        var packagePath = Path.Combine(_tempDirectory, "import-source.sxtpkg.json");
        await source.PackageService.ExportAsync(packagePath, new MigrationPackageExportOptions());

        var target = CreateService("target", CreateDevice("Other", "DifferentPC", "Other CPU", "Other GPU"));
        await target.ProfileManager.SaveProfileAsync(new PerformanceProfile
        {
            Id = "same-id",
            Name = "Existing Local",
            Notes = "keep local"
        });

        var preview = await target.PackageService.PreviewImportAsync(packagePath);
        var result = await target.PackageService.ImportAsync(
            packagePath,
            new MigrationPackageImportOptions
            {
                ImportProfiles = true,
                ImportSettings = true,
                OverwriteExistingProfiles = false
            });

        var profiles = await target.ProfileManager.LoadProfilesAsync();
        Assert.Equal(DeviceCompatibilityLevel.DifferentDevice, preview.Compatibility.Level);
        Assert.True(preview.Compatibility.ShouldWarn);
        Assert.Equal(1, preview.DeviceStrategyPackageCount);
        Assert.Equal(1, result.ImportedProfiles);
        Assert.Equal(1, result.ImportedDeviceStrategyPackages);
        Assert.True(result.ImportedSettings);
        Assert.Equal("zh-CN", target.SettingsStore.Current.LanguageCode);
        Assert.Equal(4, target.SettingsStore.Current.UnsafeRunThreshold);
        Assert.True(target.SettingsStore.Current.AutoStartMonitoringOnLaunch);
        Assert.True(target.SettingsStore.Current.LaunchAtStartup);
        Assert.Equal("keep local", profiles.Single(profile => profile.Id == "same-id").Notes);
        Assert.Equal("new profile", profiles.Single(profile => profile.Id == "new-id").Notes);
        Assert.Equal("Imported Device Pack", (await target.DeviceStrategyPackageStore.LoadPackagesAsync()).Single().Name);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
    }

    private ServiceFixture CreateService(string name, DeviceFingerprint device)
    {
        var profileManager = new JsonProfileManager(Path.Combine(_tempDirectory, name, "profiles"));
        var deviceStrategyPackageStore = new JsonDeviceStrategyPackageStore(Path.Combine(_tempDirectory, name, "device-strategies"));
        var settingsStore = new JsonAppSettingsStore(Path.Combine(_tempDirectory, name, "settings.json"));
        var packageService = new MigrationPackageService(
            profileManager,
            deviceStrategyPackageStore,
            settingsStore,
            new FixedDeviceFingerprintProvider(device));
        return new ServiceFixture(profileManager, deviceStrategyPackageStore, settingsStore, packageService);
    }

    private static DeviceFingerprint CreateDevice(string manufacturer, string model, string cpuName, string gpuName) =>
        new()
        {
            Manufacturer = manufacturer,
            Model = model,
            CpuName = cpuName,
            GpuNames = new[] { gpuName },
            TotalMemoryBytes = 16UL * 1024 * 1024 * 1024
        };

    private sealed record ServiceFixture(
        JsonProfileManager ProfileManager,
        JsonDeviceStrategyPackageStore DeviceStrategyPackageStore,
        JsonAppSettingsStore SettingsStore,
        MigrationPackageService PackageService);

    private sealed class FixedDeviceFingerprintProvider : IDeviceFingerprintProvider
    {
        private readonly DeviceFingerprint _device;

        public FixedDeviceFingerprintProvider(DeviceFingerprint device)
        {
            _device = device;
        }

        public Task<DeviceFingerprint> GetCurrentAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(_device);
    }
}
