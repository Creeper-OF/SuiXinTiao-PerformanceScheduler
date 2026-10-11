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
  using PerformanceScheduler.Infrastructure.Telemetry.NvidiaNvml;
  using PerformanceScheduler.Infrastructure.Hardware.Gpu.NvidiaNvml;
  using PerformanceScheduler.Infrastructure.Hardware.Gpu.Providers;
  using PerformanceScheduler.Infrastructure.Community;

namespace PerformanceScheduler.App.Services;

public sealed class AppRuntime : IAsyncDisposable
{
    private readonly string _applicationDataRoot;
    private readonly IStartupRecoveryManager _startupRecoveryManager;
    private readonly HttpCommunityApiClient? _communityApiClient;
    private readonly WindowsStartupRegistrationService _startupRegistrationService;
    private readonly NvidiaGpuTuningProvider _nvidiaGpuTuningProvider;
    private readonly Win32PowerChangeWatcher _powerChangeWatcher;
    private readonly object _shutdownSync = new();
    private Task? _initializationTask;
    private Task? _shutdownTask;
    private bool _shutdownRequested;
    private bool _startupRecoveryInitialized;

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
        var powerStatusProvider = new Win32PowerStatusProvider();
        var nvidiaNvmlSource = OperatingSystem.IsWindows() ? new NvidiaNvmlSource() : null;
        _nvidiaGpuTuningProvider = new NvidiaGpuTuningProvider(
            nvidiaNvmlSource,
            new FileGpuClockRecoveryStore(Path.Combine(runtimeDirectory, "gpu-clock-recovery.json")));
        var gpuTuningProviderRegistry = new GpuTuningProviderRegistry(
        [
            _nvidiaGpuTuningProvider,
            new AmdGpuTuningProvider(),
            new IntelGpuTuningProvider()
        ]);
        PowerEmergencyGate = new PowerEmergencyGate(_nvidiaGpuTuningProvider, Logger);
        _powerChangeWatcher = new Win32PowerChangeWatcher(powerStatusProvider, PowerEmergencyGate, Logger);
        SelfTestService = new HardwareSelfTestService(PowerEmergencyGate, powerPlanManager, Logger);
        var runtimeStateStore = new SqliteRuntimeStateStore(databasePath);
        TelemetryHistoryStore = new SqliteTelemetryHistoryStore(databasePath);
        var cpuAffinityManager = new Win32ProcessAffinityManager();
        var cpuSetManager = new Win32CpuSetManager();
        var processMemoryIoManager = new Win32ProcessMemoryIoManager();
        var processJobObjectManager = new Win32ProcessJobObjectManager();
        var rollbackManager = new FileRollbackManager(
            rollbackStatePath,
            powerPlanManager,
            priorityManager,
            Logger,
            cpuAffinityManager,
            cpuSetManager,
            processMemoryIoManager,
            processJobObjectManager);
        var profileManager = new JsonProfileManager(profilesDirectory);
        var deviceStrategyPackageStore = new JsonDeviceStrategyPackageStore(deviceStrategiesDirectory);
        var communityProfileCatalog = new JsonCommunityProfileCatalog(communityCatalogPath, bundledCommunityCatalogPath);
        var communityDeviceStrategyCatalog = new JsonCommunityDeviceStrategyCatalog(deviceStrategyCatalogPath, bundledDeviceStrategyCatalogPath);
        _communityApiClient = !string.IsNullOrWhiteSpace(SettingsStore.Current.CommunityServerUrl)
            ? new HttpCommunityApiClient(SettingsStore.Current.CommunityServerUrl)
            : null;
        var communitySynchronizer = new CommunityCatalogSynchronizer(
            communityProfileCatalog,
            communityDeviceStrategyCatalog,
            _communityApiClient,
            Logger,
            () => SettingsStore.Current.CommunitySyncEnabled);
        _startupRecoveryManager = new FileStartupRecoveryManager(
            startupSessionPath,
            startupFailurePath,
            rollbackManager,
            profileManager,
            Logger,
            SettingsStore.Current.AutoRollbackOnUnsafeExit,
            SettingsStore.Current.DisableProfileAfterRepeatedUnsafeRuns,
            SettingsStore.Current.PauseSchedulingAfterUnsafeExit,
            SettingsStore.Current.UnsafeRunThreshold,
            gpuRecoveryAction: ct => PowerEmergencyGate.RecoverAsync(ct));

        AppDomain.CurrentDomain.ProcessExit += OnProcessExit;

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
        var gpuPerformanceCounterSource = new WindowsGpuPerformanceCounterSource();
        var arbitrationCandidateProvider = new WindowsResourceArbitrationCandidateProvider(gpuPerformanceCounterSource);
        TelemetryObservationService = new ForegroundTelemetryObservationService(
            new Win32ForegroundWatcher(),
            new Win32ProcessInspector(),
            new TelemetryCoordinator(
            [
                 new ProcessTelemetryProvider(new ProcessMetricsCollector()),
                 new WindowsGpuProcessTelemetryProvider(gpuPerformanceCounterSource),
                 new WindowsSystemTelemetryProvider(new WindowsSystemPerformanceCounterSource()),
                 new NvidiaNvmlTelemetryProvider(new NvidiaNvmlSource()),
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
             Logger,
             null,
             arbitrationCandidateProvider);

        var resourceArbitrationProvider = new ResourceArbitrationProvider(
            arbitrationCandidateProvider,
            historyStore: TelemetryHistoryStore,
            logger: Logger);

        var targetExperienceCoordinator = new TargetExperienceAdaptiveCoordinator(
            powerPlanManager,
            rollbackManager,
            logger: Logger);

        SchedulerOrchestrator = new SchedulerOrchestrator(
            new Win32ForegroundWatcher(),
            new Win32ProcessInspector(),
            capabilityDetector,
            powerStatusProvider,
            powerPlanManager,
            priorityManager,
            profileManager,
            rollbackManager,
            new ProcessMetricsCollector(),
            runtimeStateStore,
            Logger,
            new ProfileMatcher(),
            runtimeProfileResolver: new DeviceStrategyRuntimeProfileResolver(
                deviceStrategyPackageStore,
                deviceFingerprintProvider),
            resourceArbitrationProvider: resourceArbitrationProvider,
            targetExperienceAdaptiveCoordinator: targetExperienceCoordinator,
            cpuAffinityManager: cpuAffinityManager,
            cpuSetManager: cpuSetManager,
            processMemoryIoManager: processMemoryIoManager,
            processJobObjectManager: processJobObjectManager);

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
            databasePath,
            SelfTestService,
            communitySynchronizer,
            RecoverGpuAsync);

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

    public IPowerEmergencyGate PowerEmergencyGate { get; }

    public IHardwareSelfTestService SelfTestService { get; }

    public IGpuTuningProvider GpuTuningProvider => _nvidiaGpuTuningProvider;

    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        lock (_shutdownSync)
        {
            if (_shutdownRequested)
            {
                return Task.FromException(
                    new InvalidOperationException("The application runtime is already shutting down."));
            }

            return _initializationTask ??= InitializeCoreAsync(cancellationToken);
        }
    }

    public Task ShutdownAsync()
    {
        lock (_shutdownSync)
        {
            _shutdownRequested = true;
            return _shutdownTask ??= ShutdownCoreAsync(_initializationTask);
        }
    }

    public ValueTask DisposeAsync() => new(ShutdownAsync());

    private async Task InitializeCoreAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_applicationDataRoot);
        await RuntimeStateStore.InitializeAsync(cancellationToken);
        await TelemetryHistoryStore.InitializeAsync(cancellationToken);
        StartupRecoveryResult = await _startupRecoveryManager.BeginSessionAsync(cancellationToken);
        _startupRecoveryInitialized = true;
        if (!StartupRecoveryResult.GpuRecoveryFailed)
        {
            _powerChangeWatcher.Start();
        }
        Logger.Info("Application runtime initialized.");
    }

    private async Task ShutdownCoreAsync(Task? initializationTask)
    {
        if (initializationTask is not null)
        {
            try
            {
                await initializationTask;
            }
            catch (Exception exception)
            {
                Logger.Error("Application runtime initialization did not complete before shutdown.", exception);
            }
        }

        try
        {
            await MainWindowViewModel.DisposeAsync();
        }
        catch (Exception exception)
        {
            Logger.Error("Monitoring cleanup failed during application shutdown.", exception);
        }

        try
        {
            await _powerChangeWatcher.DisposeAsync();
        }
        catch (Exception exception)
        {
            Logger.Error("Power change watcher cleanup failed during application shutdown.", exception);
        }

        try
        {
            _communityApiClient?.Dispose();
        }
        catch (Exception exception)
        {
            Logger.Error("Community API client cleanup failed during application shutdown.", exception);
        }

        var gpuRestored = false;
        try
        {
            gpuRestored = await RecoverGpuAsync(CancellationToken.None);
        }
        catch (Exception exception)
        {
            Logger.Error("GPU recovery failed during application shutdown; recovery state was preserved.", exception);
        }

        bool restored;
        try
        {
            restored = await SchedulerOrchestrator.RestoreAsync();
        }
        catch (Exception exception)
        {
            Logger.Error(
                "Application shutdown rollback failed; startup protection state was preserved.",
                exception);
            return;
        }

        if (restored &&
            gpuRestored &&
            _startupRecoveryInitialized &&
            !StartupRecoveryResult.ProfileDisableFailed)
        {
            try
            {
                await _startupRecoveryManager.MarkCleanShutdownAsync();
            }
            catch (Exception exception)
            {
                Logger.Error(
                    "Application shutdown rollback completed, but clean-shutdown state could not be persisted.",
                    exception);
            }
        }
        else if (!gpuRestored)
        {
            Logger.Error("Application shutdown left unresolved GPU recovery state; startup session was preserved for recovery.");
        }
        else if (restored)
        {
            Logger.Warn("Startup recovery did not complete; the previous startup protection state was preserved.");
        }
        else
        {
            Logger.Error("Application shutdown left unresolved rollback state; startup session was preserved for recovery.");
        }
    }

    public Task TrackAppliedProfileAsync(PerformanceProfile? profile, CancellationToken cancellationToken = default) =>
        _startupRecoveryManager.TrackAppliedProfileAsync(profile, cancellationToken);

    private async Task<bool> RecoverGpuAsync(CancellationToken cancellationToken)
    {
        if (!await _startupRecoveryManager.RecoverGpuAsync(cancellationToken))
        {
            return false;
        }

        lock (_shutdownSync)
        {
            if (_startupRecoveryInitialized && !_shutdownRequested)
            {
                _powerChangeWatcher.Start();
            }
        }

        return true;
    }

    private void OnProcessExit(object? sender, EventArgs args)
    {
        // Best effort only: abrupt process termination may never deliver this event.
        // The write-ahead GPU marker remains the authoritative startup recovery path.
        try
        {
            if (!PowerEmergencyGate.RecoverAsync(CancellationToken.None).GetAwaiter().GetResult())
            {
                Logger.Error("Process exit GPU recovery was incomplete; the recovery marker was preserved.");
            }
        }
        catch (Exception exception)
        {
            Logger.Error("Process exit GPU recovery could not complete; the recovery marker was preserved.", exception);
        }
    }
}
