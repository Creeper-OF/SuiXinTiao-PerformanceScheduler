using System.IO;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using PerformanceScheduler.App.Settings;
using PerformanceScheduler.Core.Abstractions;
using PerformanceScheduler.Core.Models;
using PerformanceScheduler.Core.Profiles.Validation;
using PerformanceScheduler.Core.Services;
using PerformanceScheduler.Infrastructure.Persistence;

namespace PerformanceScheduler.App.Services;

public sealed class MigrationPackageService
{
    private readonly IProfileManager _profileManager;
    private readonly IDeviceStrategyPackageStore _deviceStrategyPackageStore;
    private readonly JsonAppSettingsStore _settingsStore;
    private readonly IDeviceFingerprintProvider _deviceFingerprintProvider;
    private readonly DeviceCompatibilityEvaluator _compatibilityEvaluator;
    private readonly SemaphoreSlim _importGate = new(1, 1);
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
        await _importGate.WaitAsync(cancellationToken);
        try
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
            await AtomicFile.WriteJsonAsync(destinationPath, package, _jsonOptions, cancellationToken);
        }
        finally
        {
            _importGate.Release();
        }
    }

    public async Task<MigrationPackageImportPreview> PreviewImportAsync(
        string sourcePath,
        CancellationToken cancellationToken = default)
    {
        await _importGate.WaitAsync(cancellationToken);
        try
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
        finally
        {
            _importGate.Release();
        }
    }

    public async Task<MigrationPackageImportResult> ImportAsync(
        string sourcePath,
        MigrationPackageImportOptions options,
        CancellationToken cancellationToken = default)
    {
        await _importGate.WaitAsync(cancellationToken);
        try
        {
            return await ImportCoreAsync(sourcePath, options, cancellationToken);
        }
        finally
        {
            _importGate.Release();
        }
    }

    public async Task ExecuteAtomicImportAsync(
        bool includeProfiles,
        bool includeDeviceStrategyPackages,
        Func<CancellationToken, Task> importAction,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(importAction);

        await _importGate.WaitAsync(cancellationToken);
        try
        {
            var snapshots = await CaptureImportSnapshotsOrThrowAsync(
                includeProfiles,
                includeDeviceStrategyPackages,
                includeSettings: false,
                cancellationToken);
            try
            {
                await importAction(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
            }
            catch (Exception importFailure)
            {
                var rollbackFailures = await RestoreImportSnapshotsAsync(snapshots);
                if (rollbackFailures.Count == 0 && importFailure is OperationCanceledException)
                {
                    ExceptionDispatchInfo.Capture(importFailure).Throw();
                }

                throw new MigrationPackageImportException(importFailure, rollbackFailures);
            }
        }
        finally
        {
            _importGate.Release();
        }
    }

    private async Task<MigrationPackageImportResult> ImportCoreAsync(
        string sourcePath,
        MigrationPackageImportOptions options,
        CancellationToken cancellationToken)
    {
        var package = await ReadPackageAsync(sourcePath, cancellationToken);
        var currentDevice = await _deviceFingerprintProvider.GetCurrentAsync(cancellationToken);
        var compatibility = _compatibilityEvaluator.Evaluate(package.SourceDevice, currentDevice);
        var importedProfiles = 0;
        var importedDeviceStrategyPackages = 0;
        var deviceStrategyPackagesToImport = options.ImportDeviceStrategyPackages
            ? package.DeviceStrategyPackages
                .Select(deviceStrategyPackage => PrepareUntrustedDeviceStrategyPackage(deviceStrategyPackage, sourcePath))
                .ToArray()
            : Array.Empty<DeviceStrategyPackage>();

        var snapshots = await CaptureImportSnapshotsOrThrowAsync(
            options.ImportProfiles && package.Profiles.Count > 0,
            deviceStrategyPackagesToImport.Length > 0,
            options.ImportSettings && package.Settings is not null,
            cancellationToken);

        try
        {
            if (options.ImportProfiles)
            {
                var existingProfiles = await _profileManager.LoadProfilesAsync(cancellationToken);
                var profilesToImport = package.Profiles
                    .Select(profile => ProfileSafetyValidator.PrepareUntrustedImport(profile, sourcePath))
                    .ToArray();
                var existingIds = existingProfiles
                    .Select(static profile => profile.Id)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var prospectiveProfiles = existingProfiles.ToDictionary(
                    profile => profile.Id,
                    StringComparer.OrdinalIgnoreCase);
                foreach (var profile in profilesToImport)
                {
                    if (!options.OverwriteExistingProfiles && existingIds.Contains(profile.Id))
                    {
                        continue;
                    }

                    prospectiveProfiles[profile.Id] = profile;
                }

                ProfileSafetyValidator.EnsureLibraryCanPersist(prospectiveProfiles.Values, sourcePath);

                foreach (var profile in profilesToImport)
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
                foreach (var deviceStrategyPackage in deviceStrategyPackagesToImport)
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
                    settings.LanguageTransitionAnimationEnabled = imported.LanguageTransitionAnimationEnabled;
                    settings.LanguageTransitionDurationSeconds = imported.LanguageTransitionDurationSeconds;
                    settings.UiMotionEnabled = imported.UiMotionEnabled;
                    settings.UiMotionStaggerEnabled = imported.UiMotionStaggerEnabled;
                    settings.UiMotionDurationMilliseconds = imported.UiMotionDurationMilliseconds;
                    settings.AppearanceBackgroundMode = imported.AppearanceBackgroundMode;
                    settings.AppearanceBackgroundImageOpacity = imported.AppearanceBackgroundImageOpacity;
                    settings.AppearanceThemeKey = imported.AppearanceThemeKey;
                    settings.AppearanceSurfaceColorHex = imported.AppearanceSurfaceColorHex;
                    settings.AppearanceBorderColorHex = imported.AppearanceBorderColorHex;
                    settings.AppearanceWindowOverlayOpacity = imported.AppearanceWindowOverlayOpacity;
                    settings.AppearanceCardOpacity = imported.AppearanceCardOpacity;

                    // Version 1 migration packages do not embed image assets. Never import
                    // source-machine paths that may be invalid or expose personal folders.
                    settings.AppearanceBackgroundImageEnabled = false;
                    settings.AppearanceBackgroundImagePath = null;
                    settings.AppearanceSidebarBackgroundImagePath = null;
                    settings.AppearanceContentBackgroundImagePath = null;
                });
                importedSettings = true;
            }

            // Treat cancellation after the final write as a transaction failure
            // so the captured stores are restored instead of reporting success.
            cancellationToken.ThrowIfCancellationRequested();

            return new MigrationPackageImportResult
            {
                ImportedProfiles = importedProfiles,
                ImportedDeviceStrategyPackages = importedDeviceStrategyPackages,
                ImportedSettings = importedSettings,
                Compatibility = compatibility
            };
        }
        catch (Exception importFailure)
        {
            var rollbackFailures = await RestoreImportSnapshotsAsync(snapshots);
            if (rollbackFailures.Count == 0 && importFailure is OperationCanceledException)
            {
                ExceptionDispatchInfo.Capture(importFailure).Throw();
            }

            throw new MigrationPackageImportException(importFailure, rollbackFailures);
        }
    }

    private static DeviceStrategyPackage PrepareUntrustedDeviceStrategyPackage(
        DeviceStrategyPackage package,
        string sourcePath)
    {
        var profiles = (package.Profiles ?? Array.Empty<DeviceStrategyProfileItem>())
            .Select(item => item with
            {
                IsEnabledByDefault = false,
                Profile = ProfileSafetyValidator.PrepareUntrustedImport(item.Profile, sourcePath)
            })
            .ToArray();

        return package with
        {
            IsEnabled = false,
            Profiles = profiles
        };
    }

    private async Task<IReadOnlyList<ImportSnapshotEntry>> CaptureImportSnapshotsAsync(
        bool includeProfiles,
        bool includeDeviceStrategyPackages,
        bool includeSettings,
        CancellationToken cancellationToken)
    {
        var snapshots = new List<ImportSnapshotEntry>();
        if (includeProfiles)
        {
            snapshots.Add(new ImportSnapshotEntry(
                _profileManager,
                await _profileManager.CaptureImportSnapshotAsync(cancellationToken)));
        }

        if (includeDeviceStrategyPackages)
        {
            snapshots.Add(new ImportSnapshotEntry(
                _deviceStrategyPackageStore,
                await _deviceStrategyPackageStore.CaptureImportSnapshotAsync(cancellationToken)));
        }

        if (includeSettings)
        {
            snapshots.Add(new ImportSnapshotEntry(
                _settingsStore,
                await _settingsStore.CaptureImportSnapshotAsync(cancellationToken)));
        }

        return snapshots;
    }

    private async Task<IReadOnlyList<ImportSnapshotEntry>> CaptureImportSnapshotsOrThrowAsync(
        bool includeProfiles,
        bool includeDeviceStrategyPackages,
        bool includeSettings,
        CancellationToken cancellationToken)
    {
        try
        {
            return await CaptureImportSnapshotsAsync(
                includeProfiles,
                includeDeviceStrategyPackages,
                includeSettings,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception captureFailure)
        {
            // Snapshot capture does not mutate stores, but it is still part of
            // the import boundary and must use the same caller-facing error type.
            throw new MigrationPackageImportException(
                captureFailure,
                Array.Empty<MigrationPackageRollbackFailure>());
        }
    }

    private static async Task<IReadOnlyList<MigrationPackageRollbackFailure>> RestoreImportSnapshotsAsync(
        IReadOnlyList<ImportSnapshotEntry> snapshots)
    {
        var failures = new List<MigrationPackageRollbackFailure>();
        foreach (var entry in snapshots.Reverse())
        {
            try
            {
                await entry.Store.RestoreImportSnapshotAsync(entry.Snapshot, CancellationToken.None);
            }
            catch (Exception exception)
            {
                failures.Add(new MigrationPackageRollbackFailure(GetStoreName(entry.Store), exception));
            }
        }

        return failures;
    }

    private static string GetStoreName(IConfigurationImportSnapshotStore store) =>
        store switch
        {
            IProfileManager => "profiles",
            IDeviceStrategyPackageStore => "device-strategy-packages",
            JsonAppSettingsStore => "settings",
            _ => store.GetType().Name
        };

    private sealed record ImportSnapshotEntry(
        IConfigurationImportSnapshotStore Store,
        ConfigurationStoreSnapshot Snapshot);

    private async Task<MigrationPackage> ReadPackageAsync(
        string sourcePath,
        CancellationToken cancellationToken)
    {
        // The package is user-selected input, but it is still a filesystem
        // boundary. Reject reparse points before opening it so preview and
        // import never follow a redirected source path unexpectedly.
        FileStoreSnapshot.EnsureSafeFilePath(sourcePath);
        // Re-check immediately before opening; the user-writable source path
        // can change between the first validation and File.OpenRead.
        FileStoreSnapshot.EnsureSafeFilePath(sourcePath);
        await using var stream = File.OpenRead(sourcePath);
        var package = await JsonSerializer.DeserializeAsync<MigrationPackage>(stream, _jsonOptions, cancellationToken)
                      ?? throw new JsonException("The migration package is empty.");
        package = NormalizePackage(package, sourcePath);
        ConfigurationSchemaPolicy.EnsureMigrationPackageSupported(package.SchemaVersion, sourcePath);
        ProfileSafetyValidator.EnsureLibraryCanPersist(package.Profiles, sourcePath);
        foreach (var profile in package.Profiles)
        {
            if (profile is null)
            {
                continue;
            }

            ConfigurationSchemaPolicy.ValidateProfile(profile, sourcePath);
            ProfileSafetyValidator.EnsureCanPersist(profile, sourcePath);
        }

        foreach (var deviceStrategyPackage in package.DeviceStrategyPackages)
        {
            ConfigurationSchemaPolicy.ValidateDeviceStrategyPackage(deviceStrategyPackage, sourcePath);
            ProfileSafetyValidator.EnsureLibraryCanPersist(
                deviceStrategyPackage.Profiles.Select(item => item.Profile),
                sourcePath);
        }

        return package;
    }

    private static MigrationPackage NormalizePackage(MigrationPackage package, string sourcePath)
    {
        if (package.SourceDevice is null)
        {
            package = package with { SourceDevice = new DeviceFingerprint() };
        }

        var profiles = package.Profiles ?? Array.Empty<PerformanceProfile>();
        var deviceStrategyPackages = package.DeviceStrategyPackages ?? Array.Empty<DeviceStrategyPackage>();
        if (deviceStrategyPackages.Any(packageItem => packageItem is null))
        {
            throw new JsonException($"Migration package '{sourcePath}' contains a null device strategy package.");
        }

        var normalizedDeviceStrategyPackages = deviceStrategyPackages
            .Select(packageItem =>
            {
                if (string.IsNullOrWhiteSpace(packageItem.PackageId))
                {
                    throw new JsonException($"Migration package '{sourcePath}' contains a device strategy package without an id.");
                }

                if (string.IsNullOrWhiteSpace(packageItem.Name))
                {
                    throw new JsonException($"Migration package '{sourcePath}' contains a device strategy package without a name.");
                }

                if (packageItem.Profiles?.Any(item => item is null) == true)
                {
                    throw new JsonException($"Migration package '{sourcePath}' contains a null device strategy profile entry.");
                }

                return packageItem with
                {
                    TargetDevice = packageItem.TargetDevice ?? new DeviceFingerprint(),
                    Profiles = packageItem.Profiles ?? Array.Empty<DeviceStrategyProfileItem>()
                };
            })
            .ToArray();
        var duplicatePackageIds = normalizedDeviceStrategyPackages
            .GroupBy(packageItem => packageItem.PackageId.Trim(), StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();
        if (duplicatePackageIds.Length > 0)
        {
            throw new JsonException(
                $"Migration package '{sourcePath}' contains duplicate device strategy package id(s): {string.Join(", ", duplicatePackageIds)}.");
        }

        return package with
        {
            Profiles = profiles,
            DeviceStrategyPackages = normalizedDeviceStrategyPackages
        };
    }

    private static void EnsureParentDirectory(string path)
    {
        var fullPath = Path.GetFullPath(path);
        FileStoreSnapshot.EnsureSafeFilePath(fullPath);
        var parentDirectory = Path.GetDirectoryName(fullPath);
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
            UiMotionEnabled = settings.UiMotionEnabled,
            UiMotionStaggerEnabled = settings.UiMotionStaggerEnabled,
            UiMotionDurationMilliseconds = settings.UiMotionDurationMilliseconds,
            // External tool paths are machine-local and never leave the source device.
            PresentMonExecutablePath = null,
            AppearanceBackgroundImageEnabled = false,
            AppearanceBackgroundMode = settings.AppearanceBackgroundMode,
            AppearanceBackgroundImageOpacity = settings.AppearanceBackgroundImageOpacity,
            AppearanceThemeKey = settings.AppearanceThemeKey,
            AppearanceSurfaceColorHex = settings.AppearanceSurfaceColorHex,
            AppearanceBorderColorHex = settings.AppearanceBorderColorHex,
            AppearanceWindowOverlayOpacity = settings.AppearanceWindowOverlayOpacity,
            AppearanceCardOpacity = settings.AppearanceCardOpacity
        };
}
