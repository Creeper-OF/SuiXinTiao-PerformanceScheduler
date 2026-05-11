using System.IO;
using System.Text.Json;

namespace PerformanceScheduler.App.Settings;

public sealed class JsonAppSettingsStore
{
    private readonly string _settingsFilePath;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public JsonAppSettingsStore(string settingsFilePath)
    {
        _settingsFilePath = settingsFilePath;
        Current = Load();
    }

    public AppSettings Current { get; private set; }

    public void Update(Action<AppSettings> update)
    {
        var copy = Clone(Current);
        update(copy);
        copy.UnsafeRunThreshold = Math.Max(1, copy.UnsafeRunThreshold);
        copy.MonitoringIntervalSeconds = NormalizeMonitoringInterval(copy.MonitoringIntervalSeconds);
        copy.LanguageTransitionDurationSeconds = NormalizeLanguageTransitionDuration(copy.LanguageTransitionDurationSeconds);
        copy.AppearanceBackgroundImageOpacity = NormalizeAppearanceBackgroundImageOpacity(copy.AppearanceBackgroundImageOpacity);
        copy.AppearanceWindowOverlayOpacity = NormalizeAppearanceWindowOverlayOpacity(copy.AppearanceWindowOverlayOpacity);
        copy.AppearanceCardOpacity = NormalizeAppearanceCardOpacity(copy.AppearanceCardOpacity);
        copy.AppearanceThemeKey = NormalizeAppearanceThemeKey(copy.AppearanceThemeKey);
        copy.AppearanceSurfaceColorHex = NormalizeAppearanceColorHex(copy.AppearanceSurfaceColorHex);
        copy.AppearanceBorderColorHex = NormalizeAppearanceColorHex(copy.AppearanceBorderColorHex);
        copy.AppearanceBackgroundMode = NormalizeAppearanceBackgroundMode(copy.AppearanceBackgroundMode);
        Current = copy;
        Save();
    }

    private AppSettings Load()
    {
        try
        {
            if (!File.Exists(_settingsFilePath))
            {
                return new AppSettings();
            }

            var json = File.ReadAllText(_settingsFilePath);
            var settings = JsonSerializer.Deserialize<AppSettings>(json, _jsonOptions) ?? new AppSettings();
            settings.UnsafeRunThreshold = Math.Max(1, settings.UnsafeRunThreshold);
            settings.MonitoringIntervalSeconds = NormalizeMonitoringInterval(settings.MonitoringIntervalSeconds);
            settings.LanguageTransitionDurationSeconds = NormalizeLanguageTransitionDuration(settings.LanguageTransitionDurationSeconds);
            settings.AppearanceBackgroundImageOpacity = NormalizeAppearanceBackgroundImageOpacity(settings.AppearanceBackgroundImageOpacity);
            settings.AppearanceWindowOverlayOpacity = NormalizeAppearanceWindowOverlayOpacity(settings.AppearanceWindowOverlayOpacity);
            settings.AppearanceCardOpacity = NormalizeAppearanceCardOpacity(settings.AppearanceCardOpacity);
            settings.AppearanceThemeKey = NormalizeAppearanceThemeKey(settings.AppearanceThemeKey);
            settings.AppearanceSurfaceColorHex = NormalizeAppearanceColorHex(settings.AppearanceSurfaceColorHex);
            settings.AppearanceBorderColorHex = NormalizeAppearanceColorHex(settings.AppearanceBorderColorHex);
            settings.AppearanceBackgroundMode = NormalizeAppearanceBackgroundMode(settings.AppearanceBackgroundMode);
            return settings;
        }
        catch
        {
            return new AppSettings();
        }
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_settingsFilePath)!);
        File.WriteAllText(_settingsFilePath, JsonSerializer.Serialize(Current, _jsonOptions));
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

}
