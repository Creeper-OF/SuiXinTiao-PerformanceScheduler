using System.IO;
using PerformanceScheduler.App.Localization;
using PerformanceScheduler.App.Services.Diagnostics;
using PerformanceScheduler.App.Services.Telemetry;
using PerformanceScheduler.App.Settings;
using PerformanceScheduler.App.ViewModels;
using PerformanceScheduler.Core.Abstractions;
using PerformanceScheduler.Core.Models;
using PerformanceScheduler.Core.Services;
using PerformanceScheduler.Core.Telemetry.Models;
using PerformanceScheduler.Core.Telemetry.Services;
using PerformanceScheduler.Infrastructure.Capabilities;
using PerformanceScheduler.Infrastructure.Diagnostics;
using PerformanceScheduler.Infrastructure.Foreground;
using PerformanceScheduler.Infrastructure.Hardware;
using PerformanceScheduler.Infrastructure.Hardware.Gpu.Composition;
using PerformanceScheduler.Infrastructure.Hardware.Gpu.Detection;
using PerformanceScheduler.Infrastructure.Persistence;
using PerformanceScheduler.Infrastructure.Power;
using PerformanceScheduler.Infrastructure.Processes;
using PerformanceScheduler.Infrastructure.Rollback;
using PerformanceScheduler.Infrastructure.Safety;
  using PerformanceScheduler.Infrastructure.Telemetry;
  using PerformanceScheduler.Infrastructure.Telemetry.PresentMon;
  using PerformanceScheduler.Infrastructure.Telemetry.WindowsGpu;
  using PerformanceScheduler.Infrastructure.Telemetry.WindowsSystem;

namespace PerformanceScheduler.App.Services;

public sealed class AppRuntime : IAsyncDisposable
{
    private readonly string _applicationDataRoot;
    private readonly IStartupRecoveryManager _startupRecoveryManager;
    private readonly WindowsStartupRegistrationService _startupRegistrationService;
    private readonly object _shutdownSync = new();
    private Task? _shutdownTask;

    public AppRuntime()
    {
        _applicationDataRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PerformanceScheduler");

        var logsDirectory = Path.Combine(_applicationDataRoot, "logs");
        var runtimeDirectory = Path.Combine(_applicationDataRoot, "runtime");
        var configurationPaths = ConfigurationStoragePathResolver.Resolve(
            AppContext.BaseDirectory,
            _applicationDataRoot);
        var profilesDirectory = configurationPaths.ProfilesDirectory;
        var deviceStrategiesDirectory = configurationPaths.DeviceStrategiesDirectory;
        var bundledCommunityCatalogPath = Path.Combine(AppContext.BaseDirectory, "community", "sample-catalog.json");
        var bundledDeviceStrategyCatalogPath = Path.Combine(AppContext.BaseDirectory, "community", "sample-device-strategies.json");
        var localesDirectory = Path.Combine(AppContext.BaseDirectory, "locales");
        var databasePath = Path.Combine(runtimeDirectory, "scheduler.db");
        var rollbackStatePath = Path.Combine(runtimeDirectory, "rollback-state.json");
        var startupSessionPath = Path.Combine(runtimeDirectory, "startup-session.json");
        var startupFailurePath = Path.Combine(runtimeDirectory, "startup-failures.json");
        var communityCatalogPath = Path.Combine(runtimeDirectory, "community-catalog.json");
        var deviceStrategyCatalogPath = Path.Combine(runtimeDirectory, "device-strategy-catalog.json");
        var uiSettingsPath = Path.Combine(runtimeDirectory, "ui-settings.json");

        Logger = new FileAppLogger(logsDirectory);
        SettingsStore = new JsonAppSettingsStore(uiSettingsPath);
        LocalizationService = new JsonLocalizationService(localesDirectory, SettingsStore);
        _startupRegistrationService = new WindowsStartupRegistrationService();

        var powerPlanManager = new PowerCfgPowerPlanManager();
        var priorityManager = new Win32PriorityManager();
        var gpuTuningProviderRegistry = new GpuTuningProviderRegistry();
        var runtimeStateStore = new SqliteRuntimeStateStore(databasePath);
        TelemetryHistoryStore = new SqliteTelemetryHistoryStore(databasePath);
        var rollbackManager = new FileRollbackManager(rollbackStatePath, powerPlanManager, priorityManager, Logger);
        var profileManager = new JsonProfileManager(profilesDirectory);
        var deviceStrategyPackageStore = new JsonDeviceStrategyPackageStore(deviceStrategiesDirectory);
        var communityProfileCatalog = new JsonCommunityProfileCatalog(communityCatalogPath, bundledCommunityCatalogPath);
        var communityDeviceStrategyCatalog = new JsonCommunityDeviceStrategyCatalog(deviceStrategyCatalogPath, bundledDeviceStrategyCatalogPath);
        _startupRecoveryManager = new FileStartupRecoveryManager(
            startupSessionPath,
            startupFailurePath,
            rollbackManager,
            profileManager,
            Logger,
            SettingsStore.Current.AutoRollbackOnUnsafeExit,
            SettingsStore.Current.DisableProfileAfterRepeatedUnsafeRuns,
            SettingsStore.Current.PauseSchedulingAfterUnsafeExit,
            SettingsStore.Current.UnsafeRunThreshold);

        var deviceFingerprintProvider = new WindowsDeviceFingerprintProvider();
        MigrationPackageService = new MigrationPackageService(
            profileManager,
            deviceStrategyPackageStore,
            SettingsStore,
            deviceFingerprintProvider);

        var capabilityDetector = new CapabilityDetector(
            powerPlanManager,
            new WindowsGpuCapabilityProvider(),
            gpuTuningProviderRegistry);
        DiagnosticPackageService = new DiagnosticPackageService(
            runtimeStateStore,
            deviceFingerprintProvider,
            capabilityDetector,
            Logger,
            SettingsStore);
        var presentMonExecutableLocator = new PresentMonExecutableLocator(
            AppContext.BaseDirectory,
            configuredPathProvider: () => SettingsStore.Current.PresentMonExecutablePath);
        PresentMonConfigurationService = new PresentMonConfigurationService(
            SettingsStore,
            presentMonExecutableLocator);
        TelemetryObservationService = new ForegroundTelemetryObservationService(
            new Win32ForegroundWatcher(),
            new Win32ProcessInspector(),
            new TelemetryCoordinator(
            [
                 new ProcessTelemetryProvider(new ProcessMetricsCollector()),
                 new WindowsGpuProcessTelemetryProvider(new WindowsGpuPerformanceCounterSource()),
                 new WindowsSystemTelemetryProvider(new WindowsSystemPerformanceCounterSource()),
                 new PresentMonTelemetryProvider(
                    presentMonExecutableLocator,
                    new RunningPresentMonCollectorConflictDetector(),
                    new PresentMonProcessCaptureRunner(),
                    new PresentMonCsvParser(),
                    Path.Combine(runtimeDirectory, "telemetry", "presentmon"))
            ],
            new TelemetryCollectionBudget
            {
                MinimumSamplingInterval = TimeSpan.FromSeconds(1),
                MaximumProviderDuration = TimeSpan.FromSeconds(3),
                MaximumObservationDuration = TimeSpan.FromSeconds(3.5)
            }),
            TelemetryHistoryStore,
            Logger);

        SchedulerOrchestrator = new SchedulerOrchestrator(
            new Win32ForegroundWatcher(),
            new Win32ProcessInspector(),
            capabilityDetector,
            new Win32PowerStatusProvider(),
            powerPlanManager,
            priorityManager,
            profileManager,
            rollbackManager,
            new ProcessMetricsCollector(),
            runtimeStateStore,
            Logger,
            new ProfileMatcher());

        MainWindowViewModel = new MainWindowViewModel(
            SchedulerOrchestrator,
            new Win32ForegroundChangeNotifier(),
            deviceFingerprintProvider,
            MigrationPackageService,
            DiagnosticPackageService,
            TelemetryObservationService,
            PresentMonConfigurationService,
            profileManager,
            deviceStrategyPackageStore,
            communityProfileCatalog,
            communityDeviceStrategyCatalog,
            powerPlanManager,
            runtimeStateStore,
            LocalizationService,
            Logger,
            SettingsStore,
            _startupRegistrationService,
            TrackAppliedProfileAsync,
            profilesDirectory,
            deviceStrategiesDirectory,
            runtimeDirectory,
            logsDirectory,
            localesDirectory,
            uiSettingsPath,
            AppContext.BaseDirectory,
            communityCatalogPath,
            databasePath);

        ProfileManager = profileManager;
        RuntimeStateStore = runtimeStateStore;
    }

    public FileAppLogger Logger { get; }

    public JsonAppSettingsStore SettingsStore { get; }

    public JsonLocalizationService LocalizationService { get; }

    public SchedulerOrchestrator SchedulerOrchestrator { get; }

    public IProfileManager ProfileManager { get; }

    public SqliteRuntimeStateStore RuntimeStateStore { get; }

    public SqliteTelemetryHistoryStore TelemetryHistoryStore { get; }

    public MainWindowViewModel MainWindowViewModel { get; }

    public MigrationPackageService MigrationPackageService { get; }

    public DiagnosticPackageService DiagnosticPackageService { get; }

    public ForegroundTelemetryObservationService TelemetryObservationService { get; }

    public PresentMonConfigurationService PresentMonConfigurationService { get; }

    public StartupRecoveryResult StartupRecoveryResult { get; private set; } = new();

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_applicationDataRoot);
        await RuntimeStateStore.InitializeAsync(cancellationToken);
        await TelemetryHistoryStore.InitializeAsync(cancellationToken);
        StartupRecoveryResult = await _startupRecoveryManager.BeginSessionAsync(cancellationToken);
        Logger.Info("Application runtime initialized.");
    }

    public Task ShutdownAsync()
    {
        lock (_shutdownSync)
        {
            return _shutdownTask ??= ShutdownCoreAsync();
        }
    }

    public ValueTask DisposeAsync() => new(ShutdownAsync());

    private async Task ShutdownCoreAsync()
    {
        try
        {
            await MainWindowViewModel.DisposeAsync();
        }
        catch (Exception exception)
        {
            Logger.Error("Monitoring cleanup failed during application shutdown.", exception);
        }

        await SchedulerOrchestrator.RestoreAsync();
        await _startupRecoveryManager.MarkCleanShutdownAsync();
    }

    public Task TrackAppliedProfileAsync(PerformanceProfile? profile, CancellationToken cancellationToken = default) =>
        _startupRecoveryManager.TrackAppliedProfileAsync(profile, cancellationToken);
}
