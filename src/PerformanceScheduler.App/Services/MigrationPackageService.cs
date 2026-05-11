using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using PerformanceScheduler.App.Settings;
using PerformanceScheduler.Core.Abstractions;
using PerformanceScheduler.Core.Models;
using PerformanceScheduler.Core.Services;

namespace PerformanceScheduler.App.Services;

public sealed class MigrationPackageService
{
    private readonly IProfileManager _profileManager;
    private readonly IDeviceStrategyPackageStore _deviceStrategyPackageStore;
    private readonly JsonAppSettingsStore _settingsStore;
    private readonly IDeviceFingerprintProvider _deviceFingerprintProvider;
    private readonly DeviceCompatibilityEvaluator _compatibilityEvaluator;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public MigrationPackageService(
        IProfileManager profileManager,
        IDeviceStrategyPackageStore deviceStrategyPackageStore,
        JsonAppSettingsStore settingsStore,
        IDeviceFingerprintProvider deviceFingerprintProvider,
        DeviceCompatibilityEvaluator? compatibilityEvaluator = null)
    {
        _profileManager = profileManager;
        _deviceStrategyPackageStore = deviceStrategyPackageStore;
        _settingsStore = settingsStore;
        _deviceFingerprintProvider = deviceFingerprintProvider;
        _compatibilityEvaluator = compatibilityEvaluator ?? new DeviceCompatibilityEvaluator();
    }

    public async Task ExportAsync(
        string destinationPath,
        MigrationPackageExportOptions options,
        CancellationToken cancellationToken = default)
    {
        var package = new MigrationPackage
        {
            ExportedAt = DateTimeOffset.UtcNow,
            SourceDevice = await _deviceFingerprintProvider.GetCurrentAsync(cancellationToken),
            Profiles = options.IncludeProfiles
                ? await _profileManager.LoadProfilesAsync(cancellationToken)
                : Array.Empty<PerformanceProfile>(),
            DeviceStrategyPackages = options.IncludeDeviceStrategyPackages
                ? await _deviceStrategyPackageStore.LoadPackagesAsync(cancellationToken)
                : Array.Empty<DeviceStrategyPackage>(),
            Settings = options.IncludeSettings
                ? CloneSettings(_settingsStore.Current)
                : null
        };

        EnsureParentDirectory(destinationPath);
        await using var stream = File.Create(destinationPath);
        await JsonSerializer.SerializeAsync(stream, package, _jsonOptions, cancellationToken);
    }

    public async Task<MigrationPackageImportPreview> PreviewImportAsync(
        string sourcePath,
        CancellationToken cancellationToken = default)
    {
        var package = await ReadPackageAsync(sourcePath, cancellationToken);
        var currentDevice = await _deviceFingerprintProvider.GetCurrentAsync(cancellationToken);

        return new MigrationPackageImportPreview
        {
            SourceDevice = package.SourceDevice,
            Compatibility = _compatibilityEvaluator.Evaluate(package.SourceDevice, currentDevice),
            ProfileCount = package.Profiles.Count,
            DeviceStrategyPackageCount = package.DeviceStrategyPackages.Count,
            HasSettings = package.Settings is not null
        };
    }

    public async Task<MigrationPackageImportResult> ImportAsync(
        string sourcePath,
        MigrationPackageImportOptions options,
        CancellationToken cancellationToken = default)
    {
        var package = await ReadPackageAsync(sourcePath, cancellationToken);
        var currentDevice = await _deviceFingerprintProvider.GetCurrentAsync(cancellationToken);
        var compatibility = _compatibilityEvaluator.Evaluate(package.SourceDevice, currentDevice);
        var importedProfiles = 0;
        var importedDeviceStrategyPackages = 0;

        if (options.ImportProfiles)
        {
            var existingProfiles = await _profileManager.LoadProfilesAsync(cancellationToken);
            var existingIds = existingProfiles
                .Select(static profile => profile.Id)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var profile in package.Profiles)
            {
                if (!options.OverwriteExistingProfiles && existingIds.Contains(profile.Id))
                {
                    continue;
                }

                await _profileManager.SaveProfileAsync(profile, cancellationToken);
                importedProfiles++;
            }
        }

        if (options.ImportDeviceStrategyPackages)
        {
            foreach (var deviceStrategyPackage in package.DeviceStrategyPackages)
            {
                await _deviceStrategyPackageStore.SavePackageAsync(deviceStrategyPackage, cancellationToken);
                importedDeviceStrategyPackages++;
            }
        }

        var importedSettings = false;
        if (options.ImportSettings && package.Settings is not null)
        {
            var imported = package.Settings;
            _settingsStore.Update(settings =>
            {
                settings.LanguageCode = imported.LanguageCode;
                settings.PauseSchedulingAfterUnsafeExit = imported.PauseSchedulingAfterUnsafeExit;
                settings.AutoRollbackOnUnsafeExit = imported.AutoRollbackOnUnsafeExit;
                settings.DisableProfileAfterRepeatedUnsafeRuns = imported.DisableProfileAfterRepeatedUnsafeRuns;
                settings.UnsafeRunThreshold = imported.UnsafeRunThreshold;
                settings.MonitoringIntervalSeconds = imported.MonitoringIntervalSeconds;
                settings.AutoStartMonitoringOnLaunch = imported.AutoStartMonitoringOnLaunch;
                settings.LaunchAtStartup = imported.LaunchAtStartup;
            });
            importedSettings = true;
        }

        return new MigrationPackageImportResult
        {
            ImportedProfiles = importedProfiles,
            ImportedDeviceStrategyPackages = importedDeviceStrategyPackages,
            ImportedSettings = importedSettings,
            Compatibility = compatibility
        };
    }

    private async Task<MigrationPackage> ReadPackageAsync(
        string sourcePath,
        CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(sourcePath);
        return await JsonSerializer.DeserializeAsync<MigrationPackage>(stream, _jsonOptions, cancellationToken)
               ?? new MigrationPackage();
    }

    private static void EnsureParentDirectory(string path)
    {
        var parentDirectory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrWhiteSpace(parentDirectory))
        {
            Directory.CreateDirectory(parentDirectory);
        }
    }

    private static AppSettings CloneSettings(AppSettings settings) =>
        new()
        {
            LanguageCode = settings.LanguageCode,
            PauseSchedulingAfterUnsafeExit = settings.PauseSchedulingAfterUnsafeExit,
            AutoRollbackOnUnsafeExit = settings.AutoRollbackOnUnsafeExit,
            DisableProfileAfterRepeatedUnsafeRuns = settings.DisableProfileAfterRepeatedUnsafeRuns,
            UnsafeRunThreshold = settings.UnsafeRunThreshold,
            MonitoringIntervalSeconds = settings.MonitoringIntervalSeconds,
            AutoStartMonitoringOnLaunch = settings.AutoStartMonitoringOnLaunch,
            LaunchAtStartup = settings.LaunchAtStartup,
            LanguageTransitionAnimationEnabled = settings.LanguageTransitionAnimationEnabled,
            LanguageTransitionDurationSeconds = settings.LanguageTransitionDurationSeconds,
            AppearanceBackgroundImageEnabled = settings.AppearanceBackgroundImageEnabled,
            AppearanceBackgroundMode = settings.AppearanceBackgroundMode,
            AppearanceBackgroundImagePath = settings.AppearanceBackgroundImagePath,
            AppearanceSidebarBackgroundImagePath = settings.AppearanceSidebarBackgroundImagePath,
            AppearanceContentBackgroundImagePath = settings.AppearanceContentBackgroundImagePath,
            AppearanceBackgroundImageOpacity = settings.AppearanceBackgroundImageOpacity,
            AppearanceThemeKey = settings.AppearanceThemeKey,
            AppearanceSurfaceColorHex = settings.AppearanceSurfaceColorHex,
            AppearanceBorderColorHex = settings.AppearanceBorderColorHex,
            AppearanceWindowOverlayOpacity = settings.AppearanceWindowOverlayOpacity,
            AppearanceCardOpacity = settings.AppearanceCardOpacity
        };
}
