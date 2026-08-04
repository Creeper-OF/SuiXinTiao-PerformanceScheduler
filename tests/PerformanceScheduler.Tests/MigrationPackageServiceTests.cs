using PerformanceScheduler.App.Services;
using PerformanceScheduler.App.Settings;
using PerformanceScheduler.Core.Abstractions;
using PerformanceScheduler.Core.Models;
using PerformanceScheduler.Core.Services;
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
    public async Task PreviewImportAsync_RejectsFutureMigrationSchema()
    {
        var target = CreateService("future-schema", new DeviceFingerprint());
        var sourcePath = Path.Combine(_tempDirectory, "future-migration.json");
        await File.WriteAllTextAsync(
            sourcePath,
            """
            { "schemaVersion": 2, "profiles": [], "deviceStrategyPackages": [] }
            """);

        await Assert.ThrowsAsync<UnsupportedConfigurationSchemaException>(() =>
            target.PackageService.PreviewImportAsync(sourcePath));
    }

    [Fact]
    public async Task ExportAsync_WritesProfilesSettingsAndDeviceFingerprint()
    {
        var service = CreateService("source", CreateDevice("Contoso", "FalconBook", "Falcon CPU", "Falcon GPU"));
        await service.ProfileManager.SaveProfileAsync(new PerformanceProfile
        {
            Id = "game-profile",
            Name = "Game Profile",
            Match = MatchProcess("game"),
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
                        Name = "Game Profile",
                        Match = MatchProcess("game")
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
            Match = MatchProcess("existing-import"),
            Notes = "imported version"
        });
        await source.ProfileManager.SaveProfileAsync(new PerformanceProfile
        {
            Id = "new-id",
            Name = "Imported New",
            Match = MatchProcess("new-import"),
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
                        Name = "Imported New",
                        Match = MatchProcess("new-import")
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
            settings.UiMotionEnabled = false;
            settings.UiMotionStaggerEnabled = false;
            settings.UiMotionDurationMilliseconds = 460;
            settings.AppearanceThemeKey = "warmPulse";
            settings.AppearanceSurfaceColorHex = "#FFF4E8";
            settings.AppearanceBackgroundImageEnabled = true;
            settings.AppearanceBackgroundImagePath = @"C:\Users\Source\private-background.png";
            settings.PresentMonExecutablePath = @"C:\Tools\Source\PresentMon.exe";
        });
        var packagePath = Path.Combine(_tempDirectory, "import-source.sxtpkg.json");
        await source.PackageService.ExportAsync(packagePath, new MigrationPackageExportOptions());

        var target = CreateService("target", CreateDevice("Other", "DifferentPC", "Other CPU", "Other GPU"));
        target.SettingsStore.Update(settings =>
            settings.PresentMonExecutablePath = @"D:\Tools\Target\PresentMon.exe");
        await target.ProfileManager.SaveProfileAsync(new PerformanceProfile
        {
            Id = "same-id",
            Name = "Existing Local",
            Match = MatchProcess("existing-local"),
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
        Assert.False(target.SettingsStore.Current.UiMotionEnabled);
        Assert.False(target.SettingsStore.Current.UiMotionStaggerEnabled);
        Assert.Equal(460, target.SettingsStore.Current.UiMotionDurationMilliseconds);
        Assert.Equal("warmPulse", target.SettingsStore.Current.AppearanceThemeKey);
        Assert.Equal("#FFF4E8", target.SettingsStore.Current.AppearanceSurfaceColorHex);
        Assert.False(target.SettingsStore.Current.AppearanceBackgroundImageEnabled);
        Assert.Null(target.SettingsStore.Current.AppearanceBackgroundImagePath);
        Assert.Equal(@"D:\Tools\Target\PresentMon.exe", target.SettingsStore.Current.PresentMonExecutablePath);
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

    private static ProfileMatchCriteria MatchProcess(string processName) => new()
    {
        ProcessNames = new[] { processName }
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
