using System.IO;
using System.Text;
using System.Text.Json;
using PerformanceScheduler.Core.Abstractions;
using PerformanceScheduler.Core.Models;
using PerformanceScheduler.Infrastructure.Persistence;

namespace PerformanceScheduler.App.Settings;

public sealed class JsonAppSettingsStore : IConfigurationImportSnapshotStore, IConfigurationLoadIssueSource
{
    private readonly string _settingsFilePath;
    private readonly List<ConfigurationLoadIssue> _loadIssues = new();
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly object _loadIssuesSync = new();
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public JsonAppSettingsStore(string settingsFilePath)
    {
        _settingsFilePath = Path.GetFullPath(settingsFilePath);
        Current = Load();
    }

    public AppSettings Current { get; private set; }

    public IReadOnlyList<ConfigurationLoadIssue> LoadIssues
    {
        get
        {
            lock (_loadIssuesSync)
            {
                return _loadIssues.ToArray();
            }
        }
    }

    public void ReloadFromDisk()
    {
        _operationGate.Wait();
        try
        {
            Current = Load();
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<ConfigurationStoreSnapshot> CaptureImportSnapshotAsync(
        CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await FileStoreSnapshot.CaptureFileAsync(_settingsFilePath, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task RestoreImportSnapshotAsync(
        ConfigurationStoreSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await FileStoreSnapshot.RestoreFileAsync(_settingsFilePath, snapshot, cancellationToken).ConfigureAwait(false);
            Current = Load();
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public void Update(Action<AppSettings> update)
    {
        ArgumentNullException.ThrowIfNull(update);

        _operationGate.Wait();
        try
        {
            var copy = Clone(Current);
            update(copy);
            copy.UnsafeRunThreshold = Math.Max(1, copy.UnsafeRunThreshold);
            copy.MonitoringIntervalSeconds = NormalizeMonitoringInterval(copy.MonitoringIntervalSeconds);
            copy.LanguageTransitionDurationSeconds = NormalizeLanguageTransitionDuration(copy.LanguageTransitionDurationSeconds);
            copy.UiMotionDurationMilliseconds = NormalizeUiMotionDuration(copy.UiMotionDurationMilliseconds);
            copy.AppearanceBackgroundImageOpacity = NormalizeAppearanceBackgroundImageOpacity(copy.AppearanceBackgroundImageOpacity);
            copy.AppearanceWindowOverlayOpacity = NormalizeAppearanceWindowOverlayOpacity(copy.AppearanceWindowOverlayOpacity);
            copy.AppearanceCardOpacity = NormalizeAppearanceCardOpacity(copy.AppearanceCardOpacity);
            copy.AppearanceThemeKey = NormalizeAppearanceThemeKey(copy.AppearanceThemeKey);
            copy.AppearanceSurfaceColorHex = NormalizeAppearanceColorHex(copy.AppearanceSurfaceColorHex);
            copy.AppearanceBorderColorHex = NormalizeAppearanceColorHex(copy.AppearanceBorderColorHex);
            copy.AppearanceBackgroundMode = NormalizeAppearanceBackgroundMode(copy.AppearanceBackgroundMode);
            copy.PresentMonExecutablePath = NormalizeOptionalPath(copy.PresentMonExecutablePath);
            Save(copy);
            Current = copy;
            ClearLoadIssues();
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private AppSettings Load()
    {
        ClearLoadIssues();
        try
        {
            if (Directory.Exists(_settingsFilePath))
            {
                AddLoadIssue("The settings path is a directory; safe defaults were used without overwriting it.");
                return new AppSettings();
            }

            FileStoreSnapshot.EnsureSafeFilePath(_settingsFilePath);

            if (!File.Exists(_settingsFilePath))
            {
                return new AppSettings();
            }

            FileStoreSnapshot.EnsureSafeFilePath(_settingsFilePath);
            var json = File.ReadAllText(_settingsFilePath, Encoding.UTF8);
            var settings = JsonSerializer.Deserialize<AppSettings>(json, _jsonOptions);
            if (settings is null)
            {
                AddLoadIssue("The settings file contained no settings object; safe defaults were used.");
                return new AppSettings();
            }

            settings.UnsafeRunThreshold = Math.Max(1, settings.UnsafeRunThreshold);
            settings.MonitoringIntervalSeconds = NormalizeMonitoringInterval(settings.MonitoringIntervalSeconds);
            settings.LanguageTransitionDurationSeconds = NormalizeLanguageTransitionDuration(settings.LanguageTransitionDurationSeconds);
            settings.UiMotionDurationMilliseconds = NormalizeUiMotionDuration(settings.UiMotionDurationMilliseconds);
            settings.AppearanceBackgroundImageOpacity = NormalizeAppearanceBackgroundImageOpacity(settings.AppearanceBackgroundImageOpacity);
            settings.AppearanceWindowOverlayOpacity = NormalizeAppearanceWindowOverlayOpacity(settings.AppearanceWindowOverlayOpacity);
            settings.AppearanceCardOpacity = NormalizeAppearanceCardOpacity(settings.AppearanceCardOpacity);
            settings.AppearanceThemeKey = NormalizeAppearanceThemeKey(settings.AppearanceThemeKey);
            settings.AppearanceSurfaceColorHex = NormalizeAppearanceColorHex(settings.AppearanceSurfaceColorHex);
            settings.AppearanceBorderColorHex = NormalizeAppearanceColorHex(settings.AppearanceBorderColorHex);
            settings.AppearanceBackgroundMode = NormalizeAppearanceBackgroundMode(settings.AppearanceBackgroundMode);
            settings.PresentMonExecutablePath = NormalizeOptionalPath(settings.PresentMonExecutablePath);
            return settings;
        }
        catch (Exception exception)
        {
            AddLoadIssue($"The settings file could not be loaded; safe defaults were used. {exception.Message}");
            return new AppSettings();
        }
    }

    private void AddLoadIssue(string message) =>
        AddLoadIssue(new ConfigurationLoadIssue
        {
            SourcePath = _settingsFilePath,
            Message = message
        });

    private void AddLoadIssue(ConfigurationLoadIssue issue)
    {
        lock (_loadIssuesSync)
        {
            _loadIssues.Add(issue);
        }
    }

    private void ClearLoadIssues()
    {
        lock (_loadIssuesSync)
        {
            _loadIssues.Clear();
        }
    }

    private void Save(AppSettings settings)
    {
        AtomicFile.WriteAllText(_settingsFilePath, JsonSerializer.Serialize(settings, _jsonOptions));
    }

    private static AppSettings Clone(AppSettings settings) =>
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
            PresentMonExecutablePath = settings.PresentMonExecutablePath,
            LanguageTransitionAnimationEnabled = settings.LanguageTransitionAnimationEnabled,
            LanguageTransitionDurationSeconds = settings.LanguageTransitionDurationSeconds,
            UiMotionEnabled = settings.UiMotionEnabled,
            UiMotionStaggerEnabled = settings.UiMotionStaggerEnabled,
            UiMotionDurationMilliseconds = settings.UiMotionDurationMilliseconds,
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

    private static int NormalizeMonitoringInterval(int intervalSeconds) =>
        intervalSeconds switch
        {
            1 or 2 or 5 or 10 => intervalSeconds,
            _ => 2
        };

    public static double NormalizeLanguageTransitionDuration(double durationSeconds)
    {
        if (double.IsNaN(durationSeconds) || double.IsInfinity(durationSeconds))
        {
            return 0.9;
        }

        return Math.Clamp(durationSeconds, 0.2, 10);
    }

    public static double NormalizeUiMotionDuration(double durationMilliseconds)
    {
        if (double.IsNaN(durationMilliseconds) || double.IsInfinity(durationMilliseconds))
        {
            return 280;
        }

        return Math.Clamp(durationMilliseconds, 120, 800);
    }

    public static double NormalizeAppearanceBackgroundImageOpacity(double opacity)
    {
        if (double.IsNaN(opacity) || double.IsInfinity(opacity))
        {
            return 0.18;
        }

        return Math.Clamp(opacity, 0.0, 1.0);
    }

    public static double NormalizeAppearanceWindowOverlayOpacity(double opacity)
    {
        if (double.IsNaN(opacity) || double.IsInfinity(opacity))
        {
            return 0.94;
        }

        return Math.Clamp(opacity, 0.0, 0.98);
    }

    public static double NormalizeAppearanceCardOpacity(double opacity)
    {
        if (double.IsNaN(opacity) || double.IsInfinity(opacity))
        {
            return 1.0;
        }

        return Math.Clamp(opacity, 0.86, 1.0);
    }

    public static string NormalizeAppearanceThemeKey(string? themeKey)
    {
        if (string.IsNullOrWhiteSpace(themeKey))
        {
            return "cyuBlue";
        }

        return themeKey.Trim();
    }

    public static string NormalizeAppearanceColorHex(string? colorHex)
    {
        if (string.IsNullOrWhiteSpace(colorHex))
        {
            return string.Empty;
        }

        var value = colorHex.Trim();
        if (value.StartsWith('#'))
        {
            value = value[1..];
        }

        if (value.Length == 8)
        {
            value = value[2..];
        }

        if (value.Length != 6 || value.Any(character => !Uri.IsHexDigit(character)))
        {
            return string.Empty;
        }

        return $"#{value.ToUpperInvariant()}";
    }

    public static string NormalizeAppearanceBackgroundMode(string? mode)
    {
        if (string.Equals(mode, "split", StringComparison.OrdinalIgnoreCase))
        {
            return "split";
        }

        return "unified";
    }

    public static string? NormalizeOptionalPath(string? path) =>
        string.IsNullOrWhiteSpace(path) ? null : path.Trim().Trim('"');

}
