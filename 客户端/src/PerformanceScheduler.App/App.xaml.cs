using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PerformanceScheduler.App.Localization;
using PerformanceScheduler.App.Settings;
using PerformanceScheduler.App.Services;
using PerformanceScheduler.App.ViewModels;

namespace PerformanceScheduler.App;

public partial class App : Application
{
    private const string InstanceMutexName = @"Local\SuiXinTiao.PerformanceScheduler";
    private AppRuntime? _runtime;
    private SingleInstanceGuard? _singleInstanceGuard;
    private WindowsTrayNotificationService? _trayService;
    private MainWindow? _mainWindow;
    private bool _shutdownCompleted;
    private bool _shutdownInProgress;
    private bool _isExplicitExitRequested;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        try
        {
            var commandLineArgs = StartupCommandLineArgs.Parse(e.Args);

            if (string.IsNullOrWhiteSpace(commandLineArgs.ScreenshotDirectory))
            {
                if (!SingleInstanceGuard.TryAcquire(InstanceMutexName, out _singleInstanceGuard))
                {
                    var localizationService = CreateFallbackLocalizationService();
                    MessageBox.Show(
                        localizationService.Get("Dialog.AlreadyRunningMessage"),
                        localizationService.Get("Dialog.AlreadyRunningTitle"),
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                    Shutdown();
                    return;
                }
            }

            _runtime = new AppRuntime();
            await _runtime.InitializeAsync();

            _mainWindow = new MainWindow(_runtime.MainWindowViewModel);
            MainWindow = _mainWindow;
            _mainWindow.Closing += MainWindow_Closing;

            InitializeTrayService();

            if (!commandLineArgs.IsSilent)
            {
                _mainWindow.Show();
            }

            _runtime.MainWindowViewModel.ApplyStartupRecoveryResult(_runtime.StartupRecoveryResult);
            await _runtime.MainWindowViewModel.InitializeAsync();

            if (!string.IsNullOrWhiteSpace(commandLineArgs.ScreenshotDirectory))
            {
                await CaptureAllPageScreenshotsAsync(_mainWindow, commandLineArgs.ScreenshotDirectory);
                _isExplicitExitRequested = true;
                Shutdown();
                return;
            }
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

    private void InitializeTrayService()
    {
        if (_runtime is null)
        {
            return;
        }

        _trayService = new WindowsTrayNotificationService(
            showMainWindowAction: ShowMainWindow,
            isSchedulingActiveFunc: () => _runtime.MainWindowViewModel.IsMonitoringRunning && !_runtime.MainWindowViewModel.IsSchedulingSuspended,
            toggleSchedulingAction: () =>
            {
                Dispatcher.Invoke(() =>
                {
                    if (_runtime.MainWindowViewModel.ToggleMonitoringCommand.CanExecute(null))
                    {
                        _runtime.MainWindowViewModel.ToggleMonitoringCommand.Execute(null);
                    }
                });
            },
            emergencyRollbackAction: () =>
            {
                Dispatcher.Invoke(() =>
                {
                    if (_runtime.MainWindowViewModel.RestoreCommand.CanExecute(null))
                    {
                        _runtime.MainWindowViewModel.RestoreCommand.Execute(null);
                    }
                });
            },
            exitAction: () =>
            {
                Dispatcher.InvokeAsync(RequestExplicitExit);
            },
            localizationService: _runtime.LocalizationService,
            logger: _runtime.Logger);

        _trayService.Initialize();
    }

    public void ShowMainWindow()
    {
        Dispatcher.Invoke(() =>
        {
            if (_mainWindow is null)
            {
                return;
            }

            if (!_mainWindow.IsVisible)
            {
                _mainWindow.Show();
            }

            if (_mainWindow.WindowState == WindowState.Minimized)
            {
                _mainWindow.WindowState = WindowState.Normal;
            }

            _mainWindow.Activate();
            _mainWindow.Focus();
        });
    }

    public void RequestExplicitExit()
    {
        _isExplicitExitRequested = true;
        if (_mainWindow is not null)
        {
            _mainWindow.IsExplicitExitRequested = true;
            _mainWindow.Close();
        }
        else
        {
            Shutdown();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _trayService?.Dispose();
        _singleInstanceGuard?.Dispose();
        base.OnExit(e);
    }

    private async void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_shutdownCompleted)
        {
            return;
        }

        if (!_isExplicitExitRequested && (_mainWindow is null || !_mainWindow.IsExplicitExitRequested))
        {
            e.Cancel = true;
            _mainWindow?.Hide();
            return;
        }

        e.Cancel = true;
        if (_shutdownInProgress)
        {
            return;
        }

        _shutdownInProgress = true;
        _trayService?.Dispose();
        _trayService = null;
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

    private static async Task CaptureAllPageScreenshotsAsync(MainWindow window, string outputDir)
    {
        Directory.CreateDirectory(outputDir);
        window.Show();
        window.Width = 1360;
        window.Height = 860;
        window.UpdateLayout();
        await Task.Delay(400);

        string[] pageNames =
        [
            "01-总览-Overview",
            "02-应用策略-AppProfiles",
            "03-全局策略-GlobalPolicies",
            "04-性能监控-Performance",
            "05-方案库-StrategyLibrary",
            "06-社区-Community",
            "07-安全与恢复-Safety",
            "08-设置-Settings"
        ];

        for (int i = 0; i < pageNames.Length; i++)
        {
            if (window.DataContext is MainWindowViewModel vm)
            {
                vm.SelectedPageIndex = i;
            }

            window.UpdateLayout();
            await Task.Delay(300);

            var dpiX = 96.0;
            var dpiY = 96.0;
            var width = (int)window.ActualWidth;
            var height = (int)window.ActualHeight;
            if (width <= 0) width = 1360;
            if (height <= 0) height = 860;

            var rtb = new RenderTargetBitmap(width, height, dpiX, dpiY, PixelFormats.Pbgra32);
            rtb.Render(window);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(rtb));

            var filePath = Path.Combine(outputDir, $"{pageNames[i]}.png");
            using var stream = File.Create(filePath);
            encoder.Save(stream);
        }
    }
}
