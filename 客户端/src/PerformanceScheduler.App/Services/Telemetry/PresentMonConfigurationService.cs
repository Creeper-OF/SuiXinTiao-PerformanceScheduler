using System.IO;
using PerformanceScheduler.App.Settings;
using PerformanceScheduler.Infrastructure.Telemetry.PresentMon;

namespace PerformanceScheduler.App.Services.Telemetry;

public sealed class PresentMonConfigurationService
{
    private readonly JsonAppSettingsStore _settingsStore;
    private readonly IPresentMonExecutableLocator _executableLocator;

    public PresentMonConfigurationService(
        JsonAppSettingsStore settingsStore,
        IPresentMonExecutableLocator executableLocator)
    {
        _settingsStore = settingsStore;
        _executableLocator = executableLocator;
    }

    public PresentMonConfigurationState GetState()
    {
        var configuredPath = _settingsStore.Current.PresentMonExecutablePath ?? string.Empty;
        var resolvedPath = _executableLocator.FindExecutable() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(configuredPath) &&
            !PresentMonExecutableLocator.IsSupportedConsoleExecutable(configuredPath))
        {
            return new PresentMonConfigurationState
            {
                Status = PresentMonConfigurationStatus.ConfiguredPathInvalid,
                ConfiguredPath = configuredPath,
                ResolvedPath = resolvedPath
            };
        }

        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            return new PresentMonConfigurationState
            {
                Status = PresentMonConfigurationStatus.Configured,
                ConfiguredPath = configuredPath,
                ResolvedPath = resolvedPath
            };
        }

        return new PresentMonConfigurationState
        {
            Status = string.IsNullOrWhiteSpace(resolvedPath)
                ? PresentMonConfigurationStatus.Unavailable
                : PresentMonConfigurationStatus.AutoDetected,
            ResolvedPath = resolvedPath
        };
    }

    public bool TryConfigure(string path)
    {
        if (!PresentMonExecutableLocator.IsSupportedConsoleExecutable(path))
        {
            return false;
        }

        var fullPath = Path.GetFullPath(path);
        _settingsStore.Update(settings => settings.PresentMonExecutablePath = fullPath);
        return true;
    }

    public void ClearConfiguration() =>
        _settingsStore.Update(settings => settings.PresentMonExecutablePath = null);
}
