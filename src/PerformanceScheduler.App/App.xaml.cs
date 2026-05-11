using System.IO;
using System.Windows;
using PerformanceScheduler.App.Localization;
using PerformanceScheduler.App.Settings;
using PerformanceScheduler.App.Services;

namespace PerformanceScheduler.App;

public partial class App : Application
{
    private AppRuntime? _runtime;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        try
        {
            _runtime = new AppRuntime();
            await _runtime.InitializeAsync();

            var mainWindow = new MainWindow(_runtime.MainWindowViewModel);
            MainWindow = mainWindow;
            mainWindow.Show();

            _runtime.MainWindowViewModel.ApplyStartupRecoveryResult(_runtime.StartupRecoveryResult);
            await _runtime.MainWindowViewModel.InitializeAsync();
        }
        catch (Exception exception)
        {
            var localizationService = CreateFallbackLocalizationService();
            MessageBox.Show(
                localizationService.Format("Dialog.StartupErrorMessage", Environment.NewLine, exception),
                localizationService.Get("Dialog.StartupErrorTitle"),
                MessageBoxButton.OK,
                MessageBoxImage.Error);

            Shutdown(-1);
        }
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        if (_runtime is not null)
        {
            await _runtime.DisposeAsync();
        }

        base.OnExit(e);
    }

    private static JsonLocalizationService CreateFallbackLocalizationService()
    {
        var localesDirectory = Path.Combine(AppContext.BaseDirectory, "locales");
        var settingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PerformanceScheduler",
            "runtime",
            "ui-settings.json");
        var settingsStore = new JsonAppSettingsStore(settingsPath);
        return new JsonLocalizationService(localesDirectory, settingsStore);
    }
}
