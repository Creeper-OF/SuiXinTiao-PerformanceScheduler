using PerformanceScheduler.App.Settings;

namespace PerformanceScheduler.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private bool TryPersistSetting(
        string settingName,
        Action<AppSettings> update,
        Action rollback)
    {
        try
        {
            _settingsStore.Update(update);
            return true;
        }
        catch (Exception exception)
        {
            try
            {
                rollback();
            }
            catch (Exception rollbackException)
            {
                _logger.Error($"Failed to restore in-memory setting '{settingName}'.", rollbackException);
            }

            _logger.Error($"Failed to persist setting '{settingName}'.", exception);
            SetMonitoringStatus("Status.SettingsSaveFailed");
            AddStatusCenterEvent(
                "Section.Settings",
                "Status.SettingsSaveFailed",
                [],
                "Warning");
            return false;
        }
    }
}
