using System.ComponentModel;
using System.IO;
using System.Windows;
using PerformanceScheduler.App.Localization;
using PerformanceScheduler.App.Settings;
using PerformanceScheduler.App.Services;

namespace PerformanceScheduler.App;

public partial class App : Application
{
    private AppRuntime? _runtime;
    private bool _shutdownCompleted;
    private bool _shutdownInProgress;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        try
        {
            _runtime = new AppRuntime();
            await _runtime.InitializeAsync();

            var mainWindow = new MainWindow(_runtime.MainWindowViewModel);
            MainWindow = mainWindow;
            mainWindow.Closing += MainWindow_Closing;
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

            if (_runtime is not null)
            {
                await TryShutdownRuntimeAsync();
            }

            Shutdown(-1);
        }
    }

    private async void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_shutdownCompleted)
        {
            return;
        }

        e.Cancel = true;
        if (_shutdownInProgress)
        {
            return;
        }

        _shutdownInProgress = true;
        await TryShutdownRuntimeAsync();
        _shutdownCompleted = true;
        _shutdownInProgress = false;

        if (sender is Window window)
        {
            window.Closing -= MainWindow_Closing;
            window.Close();
            return;
        }

        Shutdown();
    }

    private async Task TryShutdownRuntimeAsync()
    {
        try
        {
            if (_runtime is not null)
            {
                await _runtime.ShutdownAsync();
            }
        }
        catch (Exception exception)
        {
            _runtime?.Logger.Error("Application shutdown cleanup failed.", exception);
        }
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
