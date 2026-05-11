namespace PerformanceScheduler.App.Settings;

public sealed class AppSettings
{
    public string? LanguageCode { get; set; }

    public bool PauseSchedulingAfterUnsafeExit { get; set; } = true;

    public bool AutoRollbackOnUnsafeExit { get; set; } = true;

    public bool DisableProfileAfterRepeatedUnsafeRuns { get; set; } = true;

    public int UnsafeRunThreshold { get; set; } = 2;

    public int MonitoringIntervalSeconds { get; set; } = 2;

    public bool AutoStartMonitoringOnLaunch { get; set; }

    public bool LaunchAtStartup { get; set; }

    public bool LanguageTransitionAnimationEnabled { get; set; } = true;

    public double LanguageTransitionDurationSeconds { get; set; } = 0.9;

    public bool AppearanceBackgroundImageEnabled { get; set; }

    public string AppearanceBackgroundMode { get; set; } = "unified";

    public string? AppearanceBackgroundImagePath { get; set; }

    public string? AppearanceSidebarBackgroundImagePath { get; set; }

    public string? AppearanceContentBackgroundImagePath { get; set; }

    public double AppearanceBackgroundImageOpacity { get; set; } = 0.18;

    public string AppearanceThemeKey { get; set; } = "cyuBlue";

    public string AppearanceSurfaceColorHex { get; set; } = string.Empty;

    public string AppearanceBorderColorHex { get; set; } = string.Empty;

    public double AppearanceWindowOverlayOpacity { get; set; } = 0.94;

    public double AppearanceCardOpacity { get; set; } = 1.0;
}
