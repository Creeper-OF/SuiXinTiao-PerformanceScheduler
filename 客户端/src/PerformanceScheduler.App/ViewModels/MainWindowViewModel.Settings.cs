using Microsoft.Win32;
using PerformanceScheduler.App.Services.Telemetry;
using PerformanceScheduler.App.Settings;
using PerformanceScheduler.App.Ui;

namespace PerformanceScheduler.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private string _presentMonExecutablePathDisplay = string.Empty;
    private string _presentMonIntegrationStatus = string.Empty;

    public string PresentMonExecutablePathDisplay
    {
        get => _presentMonExecutablePathDisplay;
        private set => SetProperty(ref _presentMonExecutablePathDisplay, value);
    }

    public string PresentMonIntegrationStatus
    {
        get => _presentMonIntegrationStatus;
        private set => SetProperty(ref _presentMonIntegrationStatus, value);
    }

    private void LoadSettings()
    {
        var settings = _settingsStore.Current;
        _pauseSchedulingAfterUnsafeExit = settings.PauseSchedulingAfterUnsafeExit;
        _autoRollbackOnUnsafeExit = settings.AutoRollbackOnUnsafeExit;
        _disableProfileAfterRepeatedUnsafeRuns = settings.DisableProfileAfterRepeatedUnsafeRuns;
        _unsafeRunThreshold = Math.Max(1, settings.UnsafeRunThreshold);
        _monitoringIntervalSeconds = NormalizeMonitoringInterval(settings.MonitoringIntervalSeconds);
        _autoStartMonitoringOnLaunch = settings.AutoStartMonitoringOnLaunch;
        _launchAtStartup = _startupRegistrationService.IsEnabled() || settings.LaunchAtStartup;
        _languageTransitionAnimationEnabled = settings.LanguageTransitionAnimationEnabled;
        _languageTransitionDurationSeconds = JsonAppSettingsStore.NormalizeLanguageTransitionDuration(settings.LanguageTransitionDurationSeconds);
        _uiMotionEnabled = settings.UiMotionEnabled;
        _uiMotionStaggerEnabled = settings.UiMotionStaggerEnabled;
        _uiMotionDurationMilliseconds = JsonAppSettingsStore.NormalizeUiMotionDuration(settings.UiMotionDurationMilliseconds);
        _appearanceBackgroundImageEnabled = settings.AppearanceBackgroundImageEnabled;
        _selectedAppearanceBackgroundMode = JsonAppSettingsStore.NormalizeAppearanceBackgroundMode(settings.AppearanceBackgroundMode);
        _appearanceBackgroundImagePath = settings.AppearanceBackgroundImagePath ?? string.Empty;
        _appearanceSidebarBackgroundImagePath = settings.AppearanceSidebarBackgroundImagePath ?? string.Empty;
        _appearanceContentBackgroundImagePath = settings.AppearanceContentBackgroundImagePath ?? string.Empty;
        _appearanceBackgroundImageOpacity = JsonAppSettingsStore.NormalizeAppearanceBackgroundImageOpacity(settings.AppearanceBackgroundImageOpacity);
        _selectedAppearanceThemeKey = JsonAppSettingsStore.NormalizeAppearanceThemeKey(settings.AppearanceThemeKey);
        _appearanceSurfaceColorHex = JsonAppSettingsStore.NormalizeAppearanceColorHex(settings.AppearanceSurfaceColorHex);
        _appearanceBorderColorHex = JsonAppSettingsStore.NormalizeAppearanceColorHex(settings.AppearanceBorderColorHex);
        _appearanceWindowOverlayOpacity = JsonAppSettingsStore.NormalizeAppearanceWindowOverlayOpacity(settings.AppearanceWindowOverlayOpacity);
        _appearanceCardOpacity = JsonAppSettingsStore.NormalizeAppearanceCardOpacity(settings.AppearanceCardOpacity);
        LanguageTransitionCoordinator.IsEnabled = _languageTransitionAnimationEnabled;
    }

    private void NotifySettingsChanged()
    {
        OnPropertyChanged(nameof(PauseSchedulingAfterUnsafeExit));
        OnPropertyChanged(nameof(AutoRollbackOnUnsafeExit));
        OnPropertyChanged(nameof(DisableProfileAfterRepeatedUnsafeRuns));
        OnPropertyChanged(nameof(UnsafeRunThreshold));
        OnPropertyChanged(nameof(MonitoringIntervalSeconds));
        OnPropertyChanged(nameof(AutoStartMonitoringOnLaunch));
        OnPropertyChanged(nameof(LaunchAtStartup));
        OnPropertyChanged(nameof(LanguageTransitionAnimationEnabled));
        OnPropertyChanged(nameof(LanguageTransitionDurationSeconds));
        OnPropertyChanged(nameof(UiMotionEnabled));
        OnPropertyChanged(nameof(UiMotionStaggerEnabled));
        OnPropertyChanged(nameof(UiStaggeredRevealEnabled));
        OnPropertyChanged(nameof(UiMotionDurationMilliseconds));
        OnPropertyChanged(nameof(AppearanceBackgroundImageEnabled));
        OnPropertyChanged(nameof(SelectedAppearanceBackgroundMode));
        OnPropertyChanged(nameof(AppearanceBackgroundImagePath));
        OnPropertyChanged(nameof(AppearanceSidebarBackgroundImagePath));
        OnPropertyChanged(nameof(AppearanceSidebarBackgroundPreviewPath));
        OnPropertyChanged(nameof(AppearanceContentBackgroundImagePath));
        OnPropertyChanged(nameof(AppearanceBackgroundImageDisplayPath));
        OnPropertyChanged(nameof(AppearanceSplitBackgroundSummary));
        OnPropertyChanged(nameof(AppearanceBackgroundImageOpacity));
        OnPropertyChanged(nameof(AppearanceBackgroundImageVisibility));
        OnPropertyChanged(nameof(AppearanceSidebarBackgroundImageVisibility));
        OnPropertyChanged(nameof(AppearanceSidebarBackgroundPreviewVisibility));
        OnPropertyChanged(nameof(AppearanceSidebarAdjustmentVisibility));
        OnPropertyChanged(nameof(HasPendingAppearanceSidebarBackground));
        OnPropertyChanged(nameof(AppearanceContentBackgroundImageVisibility));
        OnPropertyChanged(nameof(AppearanceContentBackgroundPreviewPath));
        OnPropertyChanged(nameof(AppearanceContentBackgroundPreviewVisibility));
        OnPropertyChanged(nameof(AppearanceContentAdjustmentVisibility));
        OnPropertyChanged(nameof(HasPendingAppearanceContentBackground));
        OnPropertyChanged(nameof(SelectedAppearanceThemeKey));
        OnPropertyChanged(nameof(AppearanceSurfaceColorHex));
        OnPropertyChanged(nameof(AppearanceBorderColorHex));
        OnPropertyChanged(nameof(AppearanceWindowOverlayOpacity));
        OnPropertyChanged(nameof(AppearanceCardOpacity));
        OnPropertyChanged(nameof(AppearanceWindowOverlayBrush));
        OnPropertyChanged(nameof(AppearancePreviewOverlayBrush));
        OnPropertyChanged(nameof(AppearanceStatus));
        RefreshPresentMonConfigurationStatus();
    }

    private void ChoosePresentMonExecutable()
    {
        var dialog = new OpenFileDialog
        {
            Filter = "PresentMon Console (*.exe)|*.exe|All Files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false,
            Title = T("Dialog.SelectPresentMonExecutable")
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        if (!_presentMonConfigurationService.TryConfigure(dialog.FileName))
        {
            PresentMonIntegrationStatus = F("Status.PresentMonSelectionInvalid", dialog.FileName);
            AddStatusCenterEvent(
                "Section.PresentMonIntegration",
                "Status.PresentMonSelectionInvalid",
                [dialog.FileName],
                "Warning");
            return;
        }

        _telemetryObservationService.InvalidateProviderCapabilities();
        RefreshPresentMonConfigurationStatus();
        AddStatusCenterEvent(
            "Section.PresentMonIntegration",
            "Status.PresentMonConfigured",
            [PresentMonExecutablePathDisplay],
            "Success");
    }

    private void ClearPresentMonExecutable()
    {
        _presentMonConfigurationService.ClearConfiguration();
        _telemetryObservationService.InvalidateProviderCapabilities();
        RefreshPresentMonConfigurationStatus();
        AddStatusCenterEvent(
            "Section.PresentMonIntegration",
            "Status.PresentMonConfigurationCleared",
            [],
            "Info");
    }

    private void RefreshPresentMonConfigurationStatus()
    {
        var state = _presentMonConfigurationService.GetState();
        PresentMonExecutablePathDisplay = !string.IsNullOrWhiteSpace(state.ConfiguredPath)
            ? state.ConfiguredPath
            : !string.IsNullOrWhiteSpace(state.ResolvedPath)
                ? state.ResolvedPath
                : T("Status.NotConfigured");
        PresentMonIntegrationStatus = state.Status switch
        {
            PresentMonConfigurationStatus.Configured => F("Status.PresentMonConfigured", state.ResolvedPath),
            PresentMonConfigurationStatus.AutoDetected => F("Status.PresentMonAutoDetected", state.ResolvedPath),
            PresentMonConfigurationStatus.ConfiguredPathInvalid => F(
                "Status.PresentMonConfiguredPathInvalid",
                state.ConfiguredPath),
            _ => T("Status.PresentMonNotConfigured")
        };
    }

    private static int NormalizeMonitoringInterval(int intervalSeconds) =>
        intervalSeconds switch
        {
            1 or 2 or 5 or 10 => intervalSeconds,
            _ => 2
        };

}
