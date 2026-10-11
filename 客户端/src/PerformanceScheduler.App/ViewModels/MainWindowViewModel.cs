using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using PerformanceScheduler.App.Commands;
using PerformanceScheduler.App.Localization;
using PerformanceScheduler.App.Services;
using PerformanceScheduler.App.Services.Diagnostics;
using PerformanceScheduler.App.Services.Telemetry;
using PerformanceScheduler.App.Settings;
using PerformanceScheduler.App.Ui;
using PerformanceScheduler.App.Views;
using PerformanceScheduler.Core.Abstractions;
using PerformanceScheduler.Core.Exceptions;
using PerformanceScheduler.Core.Models;
using PerformanceScheduler.Core.Profiles.Validation;
using PerformanceScheduler.Core.Services;

namespace PerformanceScheduler.App.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject, IAsyncDisposable
{
    private readonly SchedulerOrchestrator _schedulerOrchestrator;
    private readonly IForegroundChangeNotifier _foregroundChangeNotifier;
    private readonly IDeviceFingerprintProvider _deviceFingerprintProvider;
    private readonly MigrationPackageService _migrationPackageService;
    private readonly DiagnosticPackageService _diagnosticPackageService;
    private readonly ForegroundTelemetryObservationService _telemetryObservationService;
    private readonly PresentMonConfigurationService _presentMonConfigurationService;
    private readonly IProfileManager _profileManager;
    private readonly IDeviceStrategyPackageStore _deviceStrategyPackageStore;
    private readonly ICommunityProfileCatalog _communityProfileCatalog;
    private readonly ICommunityDeviceStrategyCatalog _communityDeviceStrategyCatalog;
    private readonly ICommunityCatalogSynchronizer? _communitySynchronizer;
    private readonly Func<CancellationToken, Task<bool>>? _recoverGpuAsync;
    private readonly IPowerPlanManager _powerPlanManager;
    private readonly IRuntimeStateStore _runtimeStateStore;
    private readonly DeviceCompatibilityEvaluator _deviceCompatibilityEvaluator = new();
    private readonly JsonLocalizationService _localizationService;
    private readonly IAppLogger _logger;
    private readonly JsonAppSettingsStore _settingsStore;
    private readonly WindowsStartupRegistrationService _startupRegistrationService;
    private readonly Func<PerformanceProfile?, CancellationToken, Task>? _trackAppliedProfileAsync;
    private readonly string _profilesDirectory;
    private readonly string _deviceStrategiesDirectory;
    private readonly string _runtimeDirectory;
    private readonly string _logsDirectory;
    private readonly string _localesDirectory;
    private readonly string _settingsPath;
    private readonly string _applicationDirectory;
    private readonly string _appearanceAssetsDirectory;
    private readonly string _communityCatalogPath;
    private readonly string _databasePath;
    private readonly ConfigurationRecoveryMarkerStore _configurationRecoveryMarkerStore;
    private readonly CommunityProfileCatalogFilter _communityProfileCatalogFilter = new();
    private readonly DeviceStrategyComposer _deviceStrategyComposer = new();
    private IReadOnlyList<CommunityProfileEntry> _allCommunityEntries = Array.Empty<CommunityProfileEntry>();
    private IReadOnlyList<CommunityDeviceStrategyEntry> _allCommunityDeviceStrategyEntries = Array.Empty<CommunityDeviceStrategyEntry>();
    private CapabilitySnapshot? _capabilitySnapshot;
    private bool _capabilityRefreshFailed;
    private readonly List<StatusCenterEvent> _statusCenterEvents = new();
    private readonly HashSet<string> _reportedConfigurationIssues = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly SemaphoreSlim _configurationImportGate = new(1, 1);
    private readonly SemaphoreSlim _monitoringLifecycleGate = new(1, 1);
    private readonly object _foregroundEventSync = new();
    private readonly StartupRecoverySafetyGate _startupRecoverySafetyGate = new();
    private readonly ConfigurationRecoverySafetyGate _configurationRecoverySafetyGate = new();
    private static readonly AppearanceThemeDefinition[] AppearanceThemes =
    [
        new("cyuBlue", "Appearance.Theme.CyuBlue", Color.FromRgb(0x1F, 0x74, 0xFF), Color.FromRgb(0xEA, 0xF2, 0xFF), Color.FromRgb(0xE2, 0xEA, 0xF5), Color.FromRgb(0xF8, 0xFB, 0xFF)),
        new("mintCore", "Appearance.Theme.MintCore", Color.FromRgb(0x14, 0x93, 0x72), Color.FromRgb(0xE8, 0xF8, 0xF1), Color.FromRgb(0xD8, 0xED, 0xE6), Color.FromRgb(0xF6, 0xFC, 0xF9)),
        new("warmPulse", "Appearance.Theme.WarmPulse", Color.FromRgb(0xD9, 0x7A, 0x24), Color.FromRgb(0xFF, 0xF1, 0xDF), Color.FromRgb(0xF0, 0xDF, 0xC8), Color.FromRgb(0xFF, 0xFB, 0xF5)),
        new("slateFocus", "Appearance.Theme.SlateFocus", Color.FromRgb(0x4E, 0x65, 0xA8), Color.FromRgb(0xEE, 0xF2, 0xFF), Color.FromRgb(0xD8, 0xE0, 0xF1), Color.FromRgb(0xF7, 0xF9, 0xFE))
    ];

    private static readonly string[] AppearanceBackgroundModes = ["unified", "split"];

    private readonly IHardwareSelfTestService? _selfTestService;
    private CancellationTokenSource? _monitoringCts;
    private CancellationTokenSource? _profileHistoryCts;
    private Task? _monitoringLoopTask;
    private Task? _foregroundEventRefreshTask;
    private bool _foregroundEventRefreshQueued;
    private bool _monitoringStopRequested;
    private bool _isDisposed;
    private DateTimeOffset _lastForegroundEventAt = DateTimeOffset.MinValue;
    private SchedulerRunResult? _lastRunResult;
    private SchedulerRunHistoryItemViewModel? _selectedRuntimeHistory;
    private bool _isUpdatingLanguageSelection;
    private int _selectedPageIndex;
    private string _focusedAppName = string.Empty;
    private string _focusedAppDetails = string.Empty;
    private string _classificationSummary = string.Empty;
    private string _matchedProfileName = string.Empty;
    private string _profileReason = string.Empty;
    private string _activePowerPlanName = string.Empty;
    private string _prioritySummary = string.Empty;
    private string _metricsSummary = string.Empty;
    private double? _focusedAppCpuUsagePercent;
    private double? _focusedAppGpuUsagePercent;
    private double? _focusedAppFps;
    private string _currentPowerSourceLabel = string.Empty;
    private string _currentAppAcProfileName = string.Empty;
    private string _currentAppAcStatusText = string.Empty;
    private string _currentAppAcStatusBackground = "#FFEAF2FF";
    private string _currentAppAcStatusForeground = "#FF0066CC";
    private bool _isCurrentAppAcConfigured;
    private string _currentAppDcProfileName = string.Empty;
    private string _currentAppDcStatusText = string.Empty;
    private string _currentAppDcStatusBackground = "#FFEAF2FF";
    private string _currentAppDcStatusForeground = "#FF0066CC";
    private bool _isCurrentAppDcConfigured;
    private int _selectedGlobalPolicyDomainIndex;
    private PowerPlanOptionViewModel? _globalAcPowerPlan;
    private PowerPlanOptionViewModel? _globalDcPowerPlan;
    private int _globalAcProcessorMaxStatePercent = 100;
    private int _globalDcProcessorMaxStatePercent = 70;
    private GpuPreferenceMode _globalAcGpuMode = GpuPreferenceMode.PreferHigherPerformance;
    private GpuPreferenceMode _globalDcGpuMode = GpuPreferenceMode.DriverDefault;
    private PriorityLevel _globalAcPriority = PriorityLevel.AboveNormal;
    private PriorityLevel _globalDcPriority = PriorityLevel.Normal;
    private bool _globalAcLowerBackground = true;
    private bool _globalDcLowerBackground = true;
    private string _storageSummary = string.Empty;
    private string _dataLocationSummary = string.Empty;
    private string _monitoringStatus = string.Empty;
    private string _lastSummary = string.Empty;
    private string _lastRefreshTime = string.Empty;
    private string _profileEditorStatus = string.Empty;
    private string _communityCatalogStatus = string.Empty;
    private string _communityFilterSummary = string.Empty;
    private string _statusCenterSummary = string.Empty;
    private string _actionAuditSummary = string.Empty;
    private string _validationSummary = string.Empty;
    private string _gpuCapabilitySummary = string.Empty;
    private string _gpuControlReadinessSummary = string.Empty;
    private string _gpuAdapterSummary = string.Empty;
    private string _deviceFingerprintSummary = string.Empty;
    private bool _pauseSchedulingAfterUnsafeExit;
    private bool _autoRollbackOnUnsafeExit;
    private bool _disableProfileAfterRepeatedUnsafeRuns;
    private int _unsafeRunThreshold = 2;
    private int _monitoringIntervalSeconds = 2;
    private bool _autoStartMonitoringOnLaunch;
    private bool _launchAtStartup;
    private bool _languageTransitionAnimationEnabled = true;
    private double _languageTransitionDurationSeconds = 0.9;
    private bool _uiMotionEnabled = true;
    private bool _uiMotionStaggerEnabled = true;
    private double _uiMotionDurationMilliseconds = 280;
    private bool _appearanceBackgroundImageEnabled;
    private string _selectedAppearanceBackgroundMode = "unified";
    private string _appearanceBackgroundImagePath = string.Empty;
    private string _appearanceSidebarBackgroundImagePath = string.Empty;
    private string _appearanceContentBackgroundImagePath = string.Empty;
    private string _pendingAppearanceSidebarBackgroundImagePath = string.Empty;
    private string _pendingAppearanceContentBackgroundImagePath = string.Empty;
    private double _appearanceBackgroundImageOpacity = 0.18;
    private string _selectedAppearanceThemeKey = "cyuBlue";
    private string _appearanceSurfaceColorHex = string.Empty;
    private string _appearanceBorderColorHex = string.Empty;
    private double _appearanceWindowOverlayOpacity = 0.94;
    private double _appearanceCardOpacity = 1.0;
    private SolidColorBrush _appearanceWindowOverlayBrush = new(Color.FromArgb(0xEF, 0xFF, 0xFF, 0xFF));
    private SolidColorBrush _appearancePreviewOverlayBrush = new(Color.FromArgb(0xDF, 0xFF, 0xFF, 0xFF));
    private SolidColorBrush _appearanceSidebarOverlayBrush = new(Color.FromArgb(0xF0, 0xF8, 0xFB, 0xFF));
    private SolidColorBrush _appearanceContentOverlayBrush = new(Color.FromArgb(0xF0, 0xF8, 0xFA, 0xFD));
    private string _appearanceStatus = string.Empty;
    private string _appearanceStatusKey = "Status.AppearanceReady";
    private object[] _appearanceStatusArgs = Array.Empty<object>();
    private bool _isSchedulingSuspended;
    private bool _supportsGpuClockLimit;
    private bool _supportsGpuVoltageControl;
    private bool _supportsGpuVendorExtensions;
    private bool _supportsEfficiencyModeHint;
    private string _monitoringStatusKey = "Status.MonitorIdle";
    private object[] _monitoringStatusArgs = Array.Empty<object>();
    private string _profileEditorStatusKey = "Status.ProfileLibraryReady";
    private object[] _profileEditorStatusArgs = Array.Empty<object>();
    private string _communityCatalogStatusKey = "Status.CommunityCatalogReady";
    private object[] _communityCatalogStatusArgs = Array.Empty<object>();
    private string _validationSummaryKey = "Status.ValidationReady";
    private object[] _validationSummaryArgs = Array.Empty<object>();
    private ProfileListItemViewModel? _selectedProfile;
    private ProfileRevisionInfo? _selectedProfileRevision;
    private DeviceStrategyPackageListItemViewModel? _selectedDeviceStrategyPackage;
    private DeviceStrategyProfileItemViewModel? _selectedDeviceStrategyProfile;
    private CommunityProfileListItemViewModel? _selectedCommunityProfile;
    private CommunityDeviceStrategyListItemViewModel? _selectedCommunityDeviceStrategy;
    private LanguageOptionViewModel? _selectedLanguage;
    private CommunityCatalogSection _selectedCommunityCatalogSection = CommunityCatalogSection.DeviceStrategies;
    private string _communitySearchText = string.Empty;
    private CommunitySourceFilter _selectedCommunitySourceFilter = CommunitySourceFilter.All;
    private PowerSourceMode _selectedCommunityPowerFilter = PowerSourceMode.Any;
    private ProcessClassification? _selectedCommunityClassificationFilter;

    public MainWindowViewModel(
        SchedulerOrchestrator schedulerOrchestrator,
        IForegroundChangeNotifier foregroundChangeNotifier,
        IDeviceFingerprintProvider deviceFingerprintProvider,
        MigrationPackageService migrationPackageService,
        DiagnosticPackageService diagnosticPackageService,
        ForegroundTelemetryObservationService telemetryObservationService,
        PresentMonConfigurationService presentMonConfigurationService,
        IProfileManager profileManager,
        IDeviceStrategyPackageStore deviceStrategyPackageStore,
        ICommunityProfileCatalog communityProfileCatalog,
        ICommunityDeviceStrategyCatalog communityDeviceStrategyCatalog,
        IPowerPlanManager powerPlanManager,
        IRuntimeStateStore runtimeStateStore,
        JsonLocalizationService localizationService,
        IAppLogger logger,
        JsonAppSettingsStore settingsStore,
        WindowsStartupRegistrationService startupRegistrationService,
        Func<PerformanceProfile?, CancellationToken, Task>? trackAppliedProfileAsync,
        string profilesDirectory,
        string deviceStrategiesDirectory,
        string runtimeDirectory,
        string logsDirectory,
        string localesDirectory,
        string settingsPath,
        string applicationDirectory,
        string communityCatalogPath,
        string databasePath,
        IHardwareSelfTestService? selfTestService = null,
        ICommunityCatalogSynchronizer? communitySynchronizer = null,
        Func<CancellationToken, Task<bool>>? recoverGpuAsync = null)
    {
        _communitySynchronizer = communitySynchronizer;
        _recoverGpuAsync = recoverGpuAsync;
        _selfTestService = selfTestService;
        _schedulerOrchestrator = schedulerOrchestrator;
        _foregroundChangeNotifier = foregroundChangeNotifier;
        _deviceFingerprintProvider = deviceFingerprintProvider;
        _migrationPackageService = migrationPackageService;
        _diagnosticPackageService = diagnosticPackageService;
        _telemetryObservationService = telemetryObservationService;
        _presentMonConfigurationService = presentMonConfigurationService;
        _profileManager = profileManager;
        _deviceStrategyPackageStore = deviceStrategyPackageStore;
        _communityProfileCatalog = communityProfileCatalog;
        _communityDeviceStrategyCatalog = communityDeviceStrategyCatalog;
        _powerPlanManager = powerPlanManager;
        _runtimeStateStore = runtimeStateStore;
        _localizationService = localizationService;
        _logger = logger;
        _settingsStore = settingsStore;
        _startupRegistrationService = startupRegistrationService;
        _trackAppliedProfileAsync = trackAppliedProfileAsync;
        _profilesDirectory = profilesDirectory;
        _deviceStrategiesDirectory = deviceStrategiesDirectory;
        _runtimeDirectory = runtimeDirectory;
        _logsDirectory = logsDirectory;
        _localesDirectory = localesDirectory;
        _settingsPath = settingsPath;
        _applicationDirectory = applicationDirectory;
        _appearanceAssetsDirectory = Path.Combine(runtimeDirectory, "appearance");
        _communityCatalogPath = communityCatalogPath;
        _databasePath = databasePath;
        _configurationRecoveryMarkerStore = new ConfigurationRecoveryMarkerStore(runtimeDirectory);
        if (_configurationRecoveryMarkerStore.IsMarked)
        {
            _configurationRecoverySafetyGate.MarkRecoveryRequired();
        }

        Strings = new LocalizedStringsViewModel(_localizationService);

        RefreshCommand = new AsyncCommand(ScanNowAsync, () => CanScanNow, HandleCommandException);
        ToggleMonitoringCommand = new AsyncCommand(ToggleMonitoringAsync, () => CanToggleMonitoring, HandleCommandException);
        StartMonitoringCommand = new AsyncCommand(StartMonitoringAsync, onException: HandleCommandException);
        StopMonitoringCommand = new AsyncCommand(StopMonitoringAsync, onException: HandleCommandException);
        RestoreCommand = new AsyncCommand(RestoreAsync, onException: HandleCommandException);
        ReloadProfilesCommand = new AsyncCommand(LoadLocalLibraryAsync, onException: HandleCommandException);
        RefreshCommunityCatalogCommand = new AsyncCommand(LoadCommunityProfilesAsync, onException: HandleCommandException);
        NewProfileCommand = new AsyncCommand(CreateNewProfileAsync, onException: HandleCommandException);
        SaveProfileCommand = new AsyncCommand(SaveProfileAsync, onException: HandleCommandException);
        ImportProfilesCommand = new AsyncCommand(ImportProfilesAsync, onException: HandleCommandException);
        ExportProfilesCommand = new AsyncCommand(ExportProfilesAsync, onException: HandleCommandException);
        ImportMigrationPackageCommand = new AsyncCommand(ImportMigrationPackageAsync, onException: HandleCommandException);
        ExportMigrationPackageCommand = new AsyncCommand(ExportMigrationPackageAsync, onException: HandleCommandException);
        ExportDiagnosticPackageCommand = new AsyncCommand(ExportDiagnosticPackageAsync, onException: HandleCommandException);
        CaptureTelemetryObservationCommand = new AsyncCommand(CaptureTelemetryObservationAsync, onException: HandleCommandException);
        RefreshCapabilitiesCommand = new AsyncCommand(RefreshCapabilitiesFromCommandAsync, onException: HandleCommandException);
        ChoosePresentMonExecutableCommand = new RelayCommand(_ => ChoosePresentMonExecutable());
        ClearPresentMonExecutableCommand = new RelayCommand(_ => ClearPresentMonExecutable());
        RunFullSelfTestCommand = new AsyncCommand(RunFullSelfTestAsync, () => !IsSelfTestRunning, HandleCommandException);
        RunGpuSelfTestCommand = new AsyncCommand(RunGpuSelfTestAsync, () => !IsSelfTestRunning, HandleCommandException);
        RunPowerEmergencySelfTestCommand = new AsyncCommand(RunPowerEmergencySelfTestAsync, () => !IsSelfTestRunning, HandleCommandException);
        ImportCommunityProfileCommand = new AsyncCommand(ImportSelectedCommunityProfileAsync, onException: HandleCommandException);
        ImportCommunityDeviceStrategyCommand = new AsyncCommand(ImportSelectedCommunityDeviceStrategyAsync, onException: HandleCommandException);
        ResolveConfigurationRecoveryCommand = new AsyncCommand(
            ResolveConfigurationRecoveryAsync,
            () => _configurationRecoverySafetyGate.RequiresManualAction,
            HandleCommandException);
        CreateDeviceStrategyPackageCommand = new AsyncCommand(CreateDeviceStrategyPackageAsync, onException: HandleCommandException);
        CreateBlankDeviceStrategyPackageCommand = new AsyncCommand(CreateBlankDeviceStrategyPackageAsync, onException: HandleCommandException);
        DuplicateSelectedDeviceStrategyPackageCommand = new AsyncCommand(DuplicateSelectedDeviceStrategyPackageAsync, onException: HandleCommandException);
        ExportSelectedDeviceStrategyPackageCommand = new AsyncCommand(ExportSelectedDeviceStrategyPackageAsync, onException: HandleCommandException);
        ToggleDeviceStrategyPackageCommand = new AsyncCommand(ToggleSelectedDeviceStrategyPackageAsync, onException: HandleCommandException);
        AddProfileToDeviceStrategyCommand = new AsyncCommand(AddSelectedProfileToDeviceStrategyAsync, onException: HandleCommandException);
        RemoveProfileFromDeviceStrategyCommand = new AsyncCommand(RemoveSelectedProfileFromDeviceStrategyAsync, onException: HandleCommandException);
        RollbackProfileCommand = new AsyncCommand(RollbackProfileAsync, onException: HandleCommandException);
        EditCurrentAppRecognitionCommand = new RelayCommand(_ => EditCurrentAppRecognition());
        EditCurrentAppAcProfileCommand = new AsyncCommand(EditCurrentAppAcProfileAsync, onException: HandleCommandException);
        ResetCurrentAppAcToGlobalCommand = new AsyncCommand(ResetCurrentAppAcToGlobalAsync, onException: HandleCommandException);
        EditCurrentAppDcProfileCommand = new AsyncCommand(EditCurrentAppDcProfileAsync, onException: HandleCommandException);
        ResetCurrentAppDcToGlobalCommand = new AsyncCommand(ResetCurrentAppDcToGlobalAsync, onException: HandleCommandException);
        SaveGlobalPoliciesCommand = new AsyncCommand(SaveGlobalPoliciesAsync, onException: HandleCommandException);
        SelectGlobalPolicyDomainCommand = new RelayCommand(parameter =>
        {
            if (int.TryParse(parameter?.ToString(), out var index))
            {
                SelectedGlobalPolicyDomainIndex = index;
            }
        });
        ResumeSchedulingCommand = new RelayCommand(
            _ => ResumeScheduling(),
            _ => IsSchedulingSuspended &&
                 _startupRecoverySafetyGate.CanResume &&
                 _configurationRecoverySafetyGate.CanResume);
        OpenProfilesDirectoryCommand = new RelayCommand(_ => OpenDirectory(_profilesDirectory));
        OpenDeviceStrategiesDirectoryCommand = new RelayCommand(_ => OpenDirectory(_deviceStrategiesDirectory));
        OpenRuntimeDirectoryCommand = new RelayCommand(_ => OpenDirectory(_runtimeDirectory));
        OpenLogsDirectoryCommand = new RelayCommand(_ => OpenDirectory(_logsDirectory));
        OpenApplicationDirectoryCommand = new RelayCommand(_ => OpenDirectory(_applicationDirectory));
        ChooseAppearanceBackgroundCommand = new RelayCommand(_ => ChooseAppearanceBackgroundImage());
        ChooseAppearanceSidebarBackgroundCommand = new RelayCommand(_ => ChooseAppearanceRegionBackgroundImage("sidebar"));
        ChooseAppearanceContentBackgroundCommand = new RelayCommand(_ => ChooseAppearanceRegionBackgroundImage("content"));
        ApplyAppearanceSidebarBackgroundPreviewCommand = new RelayCommand(_ => ApplyAppearanceSidebarBackgroundPreview(), _ => HasPendingAppearanceSidebarBackground);
        CancelAppearanceSidebarBackgroundPreviewCommand = new RelayCommand(_ => CancelAppearanceSidebarBackgroundPreview(), _ => HasPendingAppearanceSidebarBackground);
        ApplyAppearanceContentBackgroundPreviewCommand = new RelayCommand(_ => ApplyAppearanceContentBackgroundPreview(), _ => HasPendingAppearanceContentBackground);
        CancelAppearanceContentBackgroundPreviewCommand = new RelayCommand(_ => CancelAppearanceContentBackgroundPreview(), _ => HasPendingAppearanceContentBackground);
        ClearAppearanceBackgroundCommand = new RelayCommand(_ => ClearAppearanceBackgroundImage(), _ => !string.IsNullOrWhiteSpace(AppearanceBackgroundImagePath));
        ClearAppearanceRegionBackgroundsCommand = new RelayCommand(
            _ => ClearAppearanceRegionBackgroundImages(),
            _ => !string.IsNullOrWhiteSpace(AppearanceSidebarBackgroundImagePath) || !string.IsNullOrWhiteSpace(AppearanceContentBackgroundImagePath));
        OpenAppearanceAssetsDirectoryCommand = new RelayCommand(_ => OpenDirectory(_appearanceAssetsDirectory));
        AddBackgroundPolicyCommand = new RelayCommand(_ => AddBackgroundPolicy());
        RemoveBackgroundPolicyCommand = new RelayCommand(
            parameter => RemoveBackgroundPolicy(parameter as BackgroundPolicyDraftItemViewModel),
            parameter => parameter is BackgroundPolicyDraftItemViewModel);
        ClearStatusCenterCommand = new RelayCommand(_ => ClearStatusCenter(), _ => StatusCenterItems.Count > 0);
        SelectCommunitySectionCommand = new RelayCommand(parameter => SelectCommunitySection(parameter?.ToString()));
        CycleLanguageCommand = new RelayCommand(_ => CycleLanguage(), _ => Languages.Count > 1);

        LoadSettings();

        foreach (var language in _localizationService.AvailableLanguages)
        {
            Languages.Add(language);
        }

        ProfileDraft.ConfigureValidation(T, F);
        ProfileDraft.ErrorsChanged += OnProfileDraftErrorsChanged;

        RebuildLocalizedOptionCollections();
        SetSelectedLanguageByCode(_localizationService.CurrentLanguageCode);
        ApplyAppearanceResources(animate: false);
        ApplyDefaultRunState();
        ApplyMonitoringStatus();
        ApplyProfileEditorStatus();
        ApplyCommunityCatalogStatus();
        ApplyAppearanceStatus();
        ApplyDiagnosticPackageStatus();
        ApplyTelemetryObservationSummary();
        if (_capabilityRefreshFailed)
        {
            CapabilityLines.Clear();
            CapabilityLines.Add(T("Capability.DetectionFailed"));
        }
        else if (_capabilitySnapshot is not null)
        {
            ApplyCapabilitySnapshot(_capabilitySnapshot);
        }
        RebuildTelemetryCapabilityLines();
        RebuildTelemetryHistoryLines();
        RefreshPresentMonConfigurationStatus();
        RefreshStatusCenterItems();
        ApplyValidationSummary();
        RefreshStorageSummary();

        _logger.EntryWritten += OnEntryWritten;
        _localizationService.LanguageChanged += OnLanguageChanged;
        _foregroundChangeNotifier.ForegroundChanged += OnForegroundChanged;
    }

    public LocalizedStringsViewModel Strings { get; }

    public int SelectedPageIndex
    {
        get => _selectedPageIndex;
        set
        {
            if (!SetProperty(ref _selectedPageIndex, value))
            {
                return;
            }

            if (value == 2)
            {
                _ = LoadGlobalPoliciesToUiAsync();
            }

            OnPropertyChanged(nameof(CurrentPageTitle));
            OnPropertyChanged(nameof(CurrentPageDescription));
        }
    }

    public string CurrentPageTitle => SelectedPageIndex switch
    {
        0 => T("Nav.OverviewTitle"),
        1 => T("Nav.AppProfilesTitle"),
        2 => T("Nav.GlobalPoliciesTitle"),
        3 => T("Nav.TelemetryTitle"),
        4 => T("Nav.LibraryTitle"),
        5 => T("Nav.CommunityTitle"),
        6 => T("Nav.SafetyTitle"),
        7 => T("Nav.SettingsTitle"),
        _ => T("Nav.OverviewTitle")
    };

    public string CurrentPageDescription => SelectedPageIndex switch
    {
        0 => T("Nav.OverviewDescription"),
        1 => T("Nav.AppProfilesDescription"),
        2 => T("Nav.GlobalPoliciesDescription"),
        3 => T("Nav.TelemetryDescription"),
        4 => T("Nav.LibraryDescription"),
        5 => T("Nav.CommunityDescription"),
        6 => T("Nav.SafetyDescription"),
        7 => T("Nav.SettingsDescription"),
        _ => T("Nav.OverviewDescription")
    };

    public ObservableCollection<LanguageOptionViewModel> Languages { get; } = new();

    public ObservableCollection<string> CapabilityLines { get; } = new();

    public ObservableCollection<string> TelemetryCapabilityLines { get; } = new();

    public ObservableCollection<LogEntryItemViewModel> RecentLogs { get; } = new();

    public ObservableCollection<string> TelemetryObservationLines { get; } = new();

    public ObservableCollection<string> TelemetryHistoryLines { get; } = new();

    public ObservableCollection<TelemetryTrendItemViewModel> TelemetryTrendItems { get; } = new();

    public bool HasTelemetryTrendItems => TelemetryTrendItems.Count > 0;

    public ObservableCollection<ProfileListItemViewModel> Profiles { get; } = new();

    public ObservableCollection<DeviceStrategyPackageListItemViewModel> DeviceStrategyPackages { get; } = new();

    public ObservableCollection<DeviceStrategyProfileItemViewModel> SelectedDeviceStrategyProfiles { get; } = new();

    public ObservableCollection<CommunityProfileListItemViewModel> CommunityProfiles { get; } = new();

    public ObservableCollection<CommunityDeviceStrategyListItemViewModel> CommunityDeviceStrategies { get; } = new();

    public ObservableCollection<StatusCenterItemViewModel> StatusCenterItems { get; } = new();

    public ObservableCollection<SchedulerActionItemViewModel> ActionAuditItems { get; } = new();

    public ObservableCollection<SchedulerRunHistoryItemViewModel> RuntimeHistoryItems { get; } = new();

    public ObservableCollection<PowerPlanOptionViewModel> PowerPlanOptions { get; } = new();

    public ObservableCollection<ProfileRevisionInfo> ProfileHistory { get; } = new();

    public ObservableCollection<string> ValidationIssues { get; } = new();

    public ObservableCollection<ValueOptionViewModel<ProcessClassification>> ClassificationOptions { get; } = new();

    public ObservableCollection<ValueOptionViewModel<PriorityLevel>> PriorityOptions { get; } = new();

    public ObservableCollection<ValueOptionViewModel<IoPriorityLevel>> IoPriorityOptions { get; } = new();

    public ObservableCollection<ValueOptionViewModel<MemoryPriorityLevel>> MemoryPriorityOptions { get; } = new();

    public ObservableCollection<ValueOptionViewModel<PowerSourceMode>> PowerSourceOptions { get; } = new();

    public ObservableCollection<ValueOptionViewModel<GpuPreferenceMode>> GpuModeOptions { get; } = new();

    public ObservableCollection<ValueOptionViewModel<TargetExperiencePriority>> TargetExperiencePriorityOptions { get; } = new();

    public ObservableCollection<ValueOptionViewModel<TargetExperienceControlMode>> TargetExperienceControlModeOptions { get; } = new();

    public ObservableCollection<ValueOptionViewModel<NoisePreference>> TargetNoisePreferenceOptions { get; } = new();

    public ObservableCollection<ValueOptionViewModel<string>> BackgroundPolicyCategoryOptions { get; } = new();

    public ObservableCollection<ValueOptionViewModel<CommunitySourceFilter>> CommunitySourceFilterOptions { get; } = new();

    public ObservableCollection<ValueOptionViewModel<PowerSourceMode>> CommunityPowerFilterOptions { get; } = new();

    public ObservableCollection<ValueOptionViewModel<ProcessClassification?>> CommunityClassificationFilterOptions { get; } = new();

    public ObservableCollection<ValueOptionViewModel<int>> MonitoringIntervalOptions { get; } = new();

    public ObservableCollection<ValueOptionViewModel<string>> AppearanceThemeOptions { get; } = new();

    public ObservableCollection<ValueOptionViewModel<string>> AppearanceBackgroundModeOptions { get; } = new();

    public ProfileDraftViewModel ProfileDraft { get; } = new();

    public ICommand RefreshCommand { get; }

    public ICommand ToggleMonitoringCommand { get; }

    public ICommand StartMonitoringCommand { get; }

    public ICommand StopMonitoringCommand { get; }

    public ICommand RestoreCommand { get; }

    public ICommand ReloadProfilesCommand { get; }

    public ICommand RefreshCommunityCatalogCommand { get; }

    public ICommand NewProfileCommand { get; }

    public ICommand SaveProfileCommand { get; }

    public ICommand ImportProfilesCommand { get; }

    public ICommand ExportProfilesCommand { get; }

    public ICommand ImportMigrationPackageCommand { get; }

    public ICommand ExportMigrationPackageCommand { get; }

    public ICommand ExportDiagnosticPackageCommand { get; }

    public ICommand CaptureTelemetryObservationCommand { get; }

    public ICommand RefreshCapabilitiesCommand { get; }

    public ICommand ChoosePresentMonExecutableCommand { get; }

    public ICommand ClearPresentMonExecutableCommand { get; }

    public ICommand RunFullSelfTestCommand { get; }

    public ICommand RunGpuSelfTestCommand { get; }

    public ICommand RunPowerEmergencySelfTestCommand { get; }

    public ICommand ImportCommunityProfileCommand { get; }

    public ICommand ImportCommunityDeviceStrategyCommand { get; }

    public ICommand ResolveConfigurationRecoveryCommand { get; }

    public ICommand CreateDeviceStrategyPackageCommand { get; }

    public ICommand CreateBlankDeviceStrategyPackageCommand { get; }

    public ICommand DuplicateSelectedDeviceStrategyPackageCommand { get; }

    public ICommand ExportSelectedDeviceStrategyPackageCommand { get; }

    public ICommand ToggleDeviceStrategyPackageCommand { get; }

    public ICommand AddProfileToDeviceStrategyCommand { get; }

    public ICommand RemoveProfileFromDeviceStrategyCommand { get; }

    public ICommand RollbackProfileCommand { get; }

    public ICommand EditCurrentAppRecognitionCommand { get; }

    public ICommand EditCurrentAppAcProfileCommand { get; }

    public ICommand ResetCurrentAppAcToGlobalCommand { get; }

    public ICommand EditCurrentAppDcProfileCommand { get; }

    public ICommand ResetCurrentAppDcToGlobalCommand { get; }

    public ICommand SaveGlobalPoliciesCommand { get; }

    public ICommand SelectGlobalPolicyDomainCommand { get; }

    public ICommand ResumeSchedulingCommand { get; }

    public ICommand OpenProfilesDirectoryCommand { get; }

    public ICommand OpenDeviceStrategiesDirectoryCommand { get; }

    public ICommand OpenRuntimeDirectoryCommand { get; }

    public ICommand OpenLogsDirectoryCommand { get; }

    public ICommand OpenApplicationDirectoryCommand { get; }

    public ICommand ChooseAppearanceBackgroundCommand { get; }

    public ICommand ChooseAppearanceSidebarBackgroundCommand { get; }

    public ICommand ChooseAppearanceContentBackgroundCommand { get; }

    public ICommand ApplyAppearanceSidebarBackgroundPreviewCommand { get; }

    public ICommand CancelAppearanceSidebarBackgroundPreviewCommand { get; }

    public ICommand ApplyAppearanceContentBackgroundPreviewCommand { get; }

    public ICommand CancelAppearanceContentBackgroundPreviewCommand { get; }

    public ICommand ClearAppearanceBackgroundCommand { get; }

    public ICommand ClearAppearanceRegionBackgroundsCommand { get; }

    public ICommand OpenAppearanceAssetsDirectoryCommand { get; }

    public ICommand AddBackgroundPolicyCommand { get; }

    public ICommand RemoveBackgroundPolicyCommand { get; }

    public ICommand ClearStatusCenterCommand { get; }

    public ICommand SelectCommunitySectionCommand { get; }

    public ICommand CycleLanguageCommand { get; }

    public void SynchronizeLanguageSelection()
    {
        if (!_isDisposed)
        {
            SetSelectedLanguageByCode(_localizationService.CurrentLanguageCode);
        }
    }

    public LanguageOptionViewModel? SelectedLanguage
    {
        get => _selectedLanguage;
        set
        {
            if (!SetProperty(ref _selectedLanguage, value) || value is null || _isUpdatingLanguageSelection)
            {
                return;
            }

            try
            {
                _localizationService.SetLanguage(value.Code);
            }
            catch
            {
                // A settings write can fail before the localization service
                // commits the new language. Keep the selector aligned with
                // the service in both that case and event-handler failures.
                SetSelectedLanguageByCode(_localizationService.CurrentLanguageCode);
                throw;
            }
        }
    }

    public ProfileListItemViewModel? SelectedProfile
    {
        get => _selectedProfile;
        set
        {
            if (SetProperty(ref _selectedProfile, value) && value is not null)
            {
                ProfileDraft.LoadFromProfile(value.Profile, PowerPlanOptions);
                SetProfileEditorStatus("Status.EditingProfile", value.Profile.Name);
                ProfileHistory.Clear();
                SelectedProfileRevision = null;
                _ = LoadProfileHistoryAsync(value.Profile.Id);
                ApplyValidationIssues(ProfileDraft.GetValidationMessages());
            }
            else if (value is null)
            {
                _profileHistoryCts?.Cancel();
                ProfileHistory.Clear();
                SelectedProfileRevision = null;
            }
        }
    }

    public ProfileRevisionInfo? SelectedProfileRevision
    {
        get => _selectedProfileRevision;
        set => SetProperty(ref _selectedProfileRevision, value);
    }

    public DeviceStrategyPackageListItemViewModel? SelectedDeviceStrategyPackage
    {
        get => _selectedDeviceStrategyPackage;
        set
        {
            if (SetProperty(ref _selectedDeviceStrategyPackage, value) && value is not null)
            {
                RebuildSelectedDeviceStrategyProfiles(value.Package);
                SetProfileEditorStatus("Status.SelectedDeviceStrategyPackage", value.Package.Name);
            }
            else if (value is null)
            {
                SelectedDeviceStrategyProfiles.Clear();
                SelectedDeviceStrategyProfile = null;
            }
        }
    }

    public DeviceStrategyProfileItemViewModel? SelectedDeviceStrategyProfile
    {
        get => _selectedDeviceStrategyProfile;
        set => SetProperty(ref _selectedDeviceStrategyProfile, value);
    }

    public CommunityProfileListItemViewModel? SelectedCommunityProfile
    {
        get => _selectedCommunityProfile;
        set => SetProperty(ref _selectedCommunityProfile, value);
    }

    public CommunityDeviceStrategyListItemViewModel? SelectedCommunityDeviceStrategy
    {
        get => _selectedCommunityDeviceStrategy;
        set => SetProperty(ref _selectedCommunityDeviceStrategy, value);
    }

    public CommunityCatalogSection SelectedCommunityCatalogSection
    {
        get => _selectedCommunityCatalogSection;
        private set
        {
            if (SetProperty(ref _selectedCommunityCatalogSection, value))
            {
                OnPropertyChanged(nameof(IsCommunityDeviceStrategiesSelected));
                OnPropertyChanged(nameof(IsCommunityProfilesSelected));
                OnPropertyChanged(nameof(CommunityDeviceStrategiesVisibility));
                OnPropertyChanged(nameof(CommunityProfilesVisibility));
                ApplyCommunityFilter();
            }
        }
    }

    public string FocusedAppName
    {
        get => _focusedAppName;
        private set => SetProperty(ref _focusedAppName, value);
    }

    public string FocusedAppDetails
    {
        get => _focusedAppDetails;
        private set => SetProperty(ref _focusedAppDetails, value);
    }

    public string ClassificationSummary
    {
        get => _classificationSummary;
        private set => SetProperty(ref _classificationSummary, value);
    }

    public string MatchedProfileName
    {
        get => _matchedProfileName;
        private set => SetProperty(ref _matchedProfileName, value);
    }

    public string CurrentAppAcProfileName
    {
        get => _currentAppAcProfileName;
        private set => SetProperty(ref _currentAppAcProfileName, value);
    }

    public string CurrentAppAcStatusText
    {
        get => _currentAppAcStatusText;
        private set => SetProperty(ref _currentAppAcStatusText, value);
    }

    public string CurrentAppAcStatusBackground
    {
        get => _currentAppAcStatusBackground;
        private set => SetProperty(ref _currentAppAcStatusBackground, value);
    }

    public string CurrentAppAcStatusForeground
    {
        get => _currentAppAcStatusForeground;
        private set => SetProperty(ref _currentAppAcStatusForeground, value);
    }

    public bool IsCurrentAppAcConfigured
    {
        get => _isCurrentAppAcConfigured;
        private set => SetProperty(ref _isCurrentAppAcConfigured, value);
    }

    public string CurrentAppDcProfileName
    {
        get => _currentAppDcProfileName;
        private set => SetProperty(ref _currentAppDcProfileName, value);
    }

    public string CurrentAppDcStatusText
    {
        get => _currentAppDcStatusText;
        private set => SetProperty(ref _currentAppDcStatusText, value);
    }

    public string CurrentAppDcStatusBackground
    {
        get => _currentAppDcStatusBackground;
        private set => SetProperty(ref _currentAppDcStatusBackground, value);
    }

    public string CurrentAppDcStatusForeground
    {
        get => _currentAppDcStatusForeground;
        private set => SetProperty(ref _currentAppDcStatusForeground, value);
    }

    public bool IsCurrentAppDcConfigured
    {
        get => _isCurrentAppDcConfigured;
        private set => SetProperty(ref _isCurrentAppDcConfigured, value);
    }

    public string ProfileReason
    {
        get => _profileReason;
        private set => SetProperty(ref _profileReason, value);
    }

    public string ActivePowerPlanName
    {
        get => _activePowerPlanName;
        private set => SetProperty(ref _activePowerPlanName, value);
    }

    public string PrioritySummary
    {
        get => _prioritySummary;
        private set => SetProperty(ref _prioritySummary, value);
    }

    public string MetricsSummary
    {
        get => _metricsSummary;
        private set => SetProperty(ref _metricsSummary, value);
    }

    public double? FocusedAppCpuUsagePercent
    {
        get => _focusedAppCpuUsagePercent;
        private set => SetProperty(ref _focusedAppCpuUsagePercent, value);
    }

    public double? FocusedAppGpuUsagePercent
    {
        get => _focusedAppGpuUsagePercent;
        private set => SetProperty(ref _focusedAppGpuUsagePercent, value);
    }

    public double? FocusedAppFps
    {
        get => _focusedAppFps;
        private set => SetProperty(ref _focusedAppFps, value);
    }

    public string CurrentPowerSourceLabel
    {
        get => _currentPowerSourceLabel;
        private set => SetProperty(ref _currentPowerSourceLabel, value);
    }

    public int SelectedGlobalPolicyDomainIndex
    {
        get => _selectedGlobalPolicyDomainIndex;
        set
        {
            if (SetProperty(ref _selectedGlobalPolicyDomainIndex, value))
            {
                OnPropertyChanged(nameof(IsGlobalPowerDomainSelected));
                OnPropertyChanged(nameof(IsGlobalPerformanceDomainSelected));
                OnPropertyChanged(nameof(IsGlobalBackgroundDomainSelected));
            }
        }
    }

    public bool IsGlobalPowerDomainSelected => SelectedGlobalPolicyDomainIndex == 0;
    public bool IsGlobalPerformanceDomainSelected => SelectedGlobalPolicyDomainIndex == 1;
    public bool IsGlobalBackgroundDomainSelected => SelectedGlobalPolicyDomainIndex == 2;

    public PowerPlanOptionViewModel? GlobalAcPowerPlan
    {
        get => _globalAcPowerPlan;
        set => SetProperty(ref _globalAcPowerPlan, value);
    }

    public PowerPlanOptionViewModel? GlobalDcPowerPlan
    {
        get => _globalDcPowerPlan;
        set => SetProperty(ref _globalDcPowerPlan, value);
    }

    public int GlobalAcProcessorMaxStatePercent
    {
        get => _globalAcProcessorMaxStatePercent;
        set => SetProperty(ref _globalAcProcessorMaxStatePercent, value);
    }

    public int GlobalDcProcessorMaxStatePercent
    {
        get => _globalDcProcessorMaxStatePercent;
        set => SetProperty(ref _globalDcProcessorMaxStatePercent, value);
    }

    public GpuPreferenceMode GlobalAcGpuMode
    {
        get => _globalAcGpuMode;
        set => SetProperty(ref _globalAcGpuMode, value);
    }

    public GpuPreferenceMode GlobalDcGpuMode
    {
        get => _globalDcGpuMode;
        set => SetProperty(ref _globalDcGpuMode, value);
    }

    public PriorityLevel GlobalAcPriority
    {
        get => _globalAcPriority;
        set => SetProperty(ref _globalAcPriority, value);
    }

    public PriorityLevel GlobalDcPriority
    {
        get => _globalDcPriority;
        set => SetProperty(ref _globalDcPriority, value);
    }

    public bool GlobalAcLowerBackground
    {
        get => _globalAcLowerBackground;
        set => SetProperty(ref _globalAcLowerBackground, value);
    }

    public bool GlobalDcLowerBackground
    {
        get => _globalDcLowerBackground;
        set => SetProperty(ref _globalDcLowerBackground, value);
    }

    public string StorageSummary
    {
        get => _storageSummary;
        private set => SetProperty(ref _storageSummary, value);
    }

    public string DataLocationSummary
    {
        get => _dataLocationSummary;
        private set => SetProperty(ref _dataLocationSummary, value);
    }

    public string MonitoringStatus
    {
        get => _monitoringStatus;
        private set => SetProperty(ref _monitoringStatus, value);
    }

    public string MonitoringModeSummary =>
        IsSchedulingSuspended
            ? T("Status.AutomationStateSuspended")
            : _monitoringCts is null
                ? T("Status.AutomationStateStopped")
                : T("Status.AutomationStateMonitoring");

    public bool IsMonitoringRunning => _monitoringCts is not null;

    public bool CanScanNow => true;

    public bool CanToggleMonitoring => true;

    public string MonitoringToggleButtonText =>
        IsSchedulingSuspended
            ? T("Button.ResumeAndStartScheduling")
            : IsMonitoringRunning
            ? T("Button.StopMonitor")
            : T("Button.StartMonitor");

    public string LastSummary
    {
        get => _lastSummary;
        private set => SetProperty(ref _lastSummary, value);
    }

    public string LastRefreshTime
    {
        get => _lastRefreshTime;
        private set => SetProperty(ref _lastRefreshTime, value);
    }

    public string ProfileEditorStatus
    {
        get => _profileEditorStatus;
        private set => SetProperty(ref _profileEditorStatus, value);
    }

    public string ValidationSummary
    {
        get => _validationSummary;
        private set => SetProperty(ref _validationSummary, value);
    }

    public string CommunityCatalogStatus
    {
        get => _communityCatalogStatus;
        private set => SetProperty(ref _communityCatalogStatus, value);
    }

    public string StatusCenterSummary
    {
        get => _statusCenterSummary;
        private set => SetProperty(ref _statusCenterSummary, value);
    }

    public string CommunityFilterSummary
    {
        get => _communityFilterSummary;
        private set => SetProperty(ref _communityFilterSummary, value);
    }

    public int StatusCenterCount => StatusCenterItems.Count;

    public int LocalProfileCount => Profiles.Count;

    public int LocalDeviceStrategyCount => DeviceStrategyPackages.Count;

    public int CommunityProfileCount => CommunityProfiles.Count;

    public int CommunityDeviceStrategyCount => CommunityDeviceStrategies.Count;

    public bool IsCommunityDeviceStrategiesSelected => SelectedCommunityCatalogSection == CommunityCatalogSection.DeviceStrategies;

    public bool IsCommunityProfilesSelected => SelectedCommunityCatalogSection == CommunityCatalogSection.Profiles;

    public Visibility CommunityDeviceStrategiesVisibility => IsCommunityDeviceStrategiesSelected ? Visibility.Visible : Visibility.Collapsed;

    public Visibility CommunityProfilesVisibility => IsCommunityProfilesSelected ? Visibility.Visible : Visibility.Collapsed;

    public string NavigationNoticesSummary => F("Status.NavigationNoticesSummaryFormat", StatusCenterCount);

    public string NavigationProfilesSummary => F("Status.NavigationProfilesSummaryFormat", LocalProfileCount, LocalDeviceStrategyCount);

    public string NavigationCommunitySummary => F("Status.NavigationCommunitySummaryFormat", CommunityProfileCount + CommunityDeviceStrategyCount);

    public string GpuCapabilitySummary
    {
        get => _gpuCapabilitySummary;
        private set => SetProperty(ref _gpuCapabilitySummary, value);
    }

    public string GpuControlReadinessSummary
    {
        get => _gpuControlReadinessSummary;
        private set => SetProperty(ref _gpuControlReadinessSummary, value);
    }

    public string GpuAdapterSummary
    {
        get => _gpuAdapterSummary;
        private set => SetProperty(ref _gpuAdapterSummary, value);
    }

    public string DeviceFingerprintSummary
    {
        get => _deviceFingerprintSummary;
        private set => SetProperty(ref _deviceFingerprintSummary, value);
    }

    public string CommunitySearchText
    {
        get => _communitySearchText;
        set
        {
            if (SetProperty(ref _communitySearchText, value))
            {
                ApplyCommunityFilter();
            }
        }
    }

    public CommunitySourceFilter SelectedCommunitySourceFilter
    {
        get => _selectedCommunitySourceFilter;
        set
        {
            if (SetProperty(ref _selectedCommunitySourceFilter, value))
            {
                ApplyCommunityFilter();
            }
        }
    }

    public PowerSourceMode SelectedCommunityPowerFilter
    {
        get => _selectedCommunityPowerFilter;
        set
        {
            if (SetProperty(ref _selectedCommunityPowerFilter, value))
            {
                ApplyCommunityFilter();
            }
        }
    }

    public ProcessClassification? SelectedCommunityClassificationFilter
    {
        get => _selectedCommunityClassificationFilter;
        set
        {
            if (SetProperty(ref _selectedCommunityClassificationFilter, value))
            {
                ApplyCommunityFilter();
            }
        }
    }

    public bool PauseSchedulingAfterUnsafeExit
    {
        get => _pauseSchedulingAfterUnsafeExit;
        set
        {
            var previous = _pauseSchedulingAfterUnsafeExit;
            if (!SetProperty(ref _pauseSchedulingAfterUnsafeExit, value))
            {
                return;
            }

            TryPersistSetting(
                nameof(PauseSchedulingAfterUnsafeExit),
                settings => settings.PauseSchedulingAfterUnsafeExit = value,
                () => SetProperty(ref _pauseSchedulingAfterUnsafeExit, previous, nameof(PauseSchedulingAfterUnsafeExit)));
        }
    }

    public bool AutoRollbackOnUnsafeExit
    {
        get => _autoRollbackOnUnsafeExit;
        set
        {
            var previous = _autoRollbackOnUnsafeExit;
            if (!SetProperty(ref _autoRollbackOnUnsafeExit, value))
            {
                return;
            }

            TryPersistSetting(
                nameof(AutoRollbackOnUnsafeExit),
                settings => settings.AutoRollbackOnUnsafeExit = value,
                () => SetProperty(ref _autoRollbackOnUnsafeExit, previous, nameof(AutoRollbackOnUnsafeExit)));
        }
    }

    public bool DisableProfileAfterRepeatedUnsafeRuns
    {
        get => _disableProfileAfterRepeatedUnsafeRuns;
        set
        {
            var previous = _disableProfileAfterRepeatedUnsafeRuns;
            if (!SetProperty(ref _disableProfileAfterRepeatedUnsafeRuns, value))
            {
                return;
            }

            TryPersistSetting(
                nameof(DisableProfileAfterRepeatedUnsafeRuns),
                settings => settings.DisableProfileAfterRepeatedUnsafeRuns = value,
                () => SetProperty(ref _disableProfileAfterRepeatedUnsafeRuns, previous, nameof(DisableProfileAfterRepeatedUnsafeRuns)));
        }
    }

    public int UnsafeRunThreshold
    {
        get => _unsafeRunThreshold;
        set
        {
            var normalized = Math.Max(1, value);
            var previous = _unsafeRunThreshold;
            if (!SetProperty(ref _unsafeRunThreshold, normalized))
            {
                return;
            }

            TryPersistSetting(
                nameof(UnsafeRunThreshold),
                settings => settings.UnsafeRunThreshold = normalized,
                () => SetProperty(ref _unsafeRunThreshold, previous, nameof(UnsafeRunThreshold)));
        }
    }

    public int MonitoringIntervalSeconds
    {
        get => _monitoringIntervalSeconds;
        set
        {
            var normalized = NormalizeMonitoringInterval(value);
            var previous = _monitoringIntervalSeconds;
            if (!SetProperty(ref _monitoringIntervalSeconds, normalized))
            {
                return;
            }

            if (TryPersistSetting(
                    nameof(MonitoringIntervalSeconds),
                    settings => settings.MonitoringIntervalSeconds = normalized,
                    () => SetProperty(ref _monitoringIntervalSeconds, previous, nameof(MonitoringIntervalSeconds))) &&
                IsMonitoringRunning)
            {
                SetMonitoringStatus("Status.MonitoringActive", normalized);
            }
        }
    }

    public bool LaunchAtStartup
    {
        get => _launchAtStartup;
        set
        {
            var previous = _launchAtStartup;
            if (!SetProperty(ref _launchAtStartup, value))
            {
                return;
            }

            try
            {
                _startupRegistrationService.SetEnabled(value);
            }
            catch (Exception exception)
            {
                SetProperty(ref _launchAtStartup, previous, nameof(LaunchAtStartup));
                _logger.Error("Failed to update Windows startup registration.", exception);
                SetMonitoringStatus("Status.SettingsSaveFailed");
                AddStatusCenterEvent("Section.Settings", "Status.SettingsSaveFailed", [], "Warning");
                return;
            }

            TryPersistSetting(
                nameof(LaunchAtStartup),
                settings => settings.LaunchAtStartup = value,
                () =>
                {
                    SetProperty(ref _launchAtStartup, previous, nameof(LaunchAtStartup));
                    _startupRegistrationService.SetEnabled(previous);
                });
        }
    }

    public bool AutoStartMonitoringOnLaunch
    {
        get => _autoStartMonitoringOnLaunch;
        set
        {
            var previous = _autoStartMonitoringOnLaunch;
            if (!SetProperty(ref _autoStartMonitoringOnLaunch, value))
            {
                return;
            }

            TryPersistSetting(
                nameof(AutoStartMonitoringOnLaunch),
                settings => settings.AutoStartMonitoringOnLaunch = value,
                () => SetProperty(ref _autoStartMonitoringOnLaunch, previous, nameof(AutoStartMonitoringOnLaunch)));
        }
    }

    public bool LanguageTransitionAnimationEnabled
    {
        get => _languageTransitionAnimationEnabled;
        set
        {
            var previous = _languageTransitionAnimationEnabled;
            if (!SetProperty(ref _languageTransitionAnimationEnabled, value))
            {
                return;
            }

            if (TryPersistSetting(
                    nameof(LanguageTransitionAnimationEnabled),
                    settings => settings.LanguageTransitionAnimationEnabled = value,
                    () => SetProperty(ref _languageTransitionAnimationEnabled, previous, nameof(LanguageTransitionAnimationEnabled))))
            {
                LanguageTransitionCoordinator.IsEnabled = value;
            }
        }
    }

    public double LanguageTransitionDurationSeconds
    {
        get => _languageTransitionDurationSeconds;
        set
        {
            var normalized = JsonAppSettingsStore.NormalizeLanguageTransitionDuration(value);
            var previous = _languageTransitionDurationSeconds;
            if (!SetProperty(ref _languageTransitionDurationSeconds, normalized))
            {
                return;
            }

            TryPersistSetting(
                nameof(LanguageTransitionDurationSeconds),
                settings => settings.LanguageTransitionDurationSeconds = normalized,
                () => SetProperty(ref _languageTransitionDurationSeconds, previous, nameof(LanguageTransitionDurationSeconds)));
        }
    }

    public string ActionAuditSummary
    {
        get => _actionAuditSummary;
        private set => SetProperty(ref _actionAuditSummary, value);
    }

    public SchedulerRunHistoryItemViewModel? SelectedRuntimeHistory
    {
        get => _selectedRuntimeHistory;
        set
        {
            if (!SetProperty(ref _selectedRuntimeHistory, value))
            {
                return;
            }

            if (value is null)
            {
                RefreshActionAudit(Array.Empty<SchedulerActionResult>());
                RefreshRuntimeHistoryDetail(null);
            }
            else
            {
                RefreshActionAudit(value.Entry.Actions);
                RefreshRuntimeHistoryDetail(value.Entry);
            }
        }
    }

    public bool UiMotionEnabled
    {
        get => _uiMotionEnabled;
        set
        {
            var previous = _uiMotionEnabled;
            if (!SetProperty(ref _uiMotionEnabled, value))
            {
                return;
            }

            if (!TryPersistSetting(
                    nameof(UiMotionEnabled),
                    settings => settings.UiMotionEnabled = value,
                    () =>
                    {
                        SetProperty(ref _uiMotionEnabled, previous, nameof(UiMotionEnabled));
                        OnPropertyChanged(nameof(UiStaggeredRevealEnabled));
                    }))
            {
                return;
            }

            OnPropertyChanged(nameof(UiStaggeredRevealEnabled));
            if (!value)
            {
                ApplyAppearanceResources();
            }
        }
    }

    public bool UiMotionStaggerEnabled
    {
        get => _uiMotionStaggerEnabled;
        set
        {
            var previous = _uiMotionStaggerEnabled;
            if (!SetProperty(ref _uiMotionStaggerEnabled, value))
            {
                return;
            }

            if (!TryPersistSetting(
                    nameof(UiMotionStaggerEnabled),
                    settings => settings.UiMotionStaggerEnabled = value,
                    () =>
                    {
                        SetProperty(ref _uiMotionStaggerEnabled, previous, nameof(UiMotionStaggerEnabled));
                        OnPropertyChanged(nameof(UiStaggeredRevealEnabled));
                    }))
            {
                return;
            }

            OnPropertyChanged(nameof(UiStaggeredRevealEnabled));
        }
    }

    public bool UiStaggeredRevealEnabled => UiMotionEnabled && UiMotionStaggerEnabled;

    public double UiMotionDurationMilliseconds
    {
        get => _uiMotionDurationMilliseconds;
        set
        {
            var normalized = JsonAppSettingsStore.NormalizeUiMotionDuration(value);
            var previous = _uiMotionDurationMilliseconds;
            if (!SetProperty(ref _uiMotionDurationMilliseconds, normalized))
            {
                return;
            }

            TryPersistSetting(
                nameof(UiMotionDurationMilliseconds),
                settings => settings.UiMotionDurationMilliseconds = normalized,
                () => SetProperty(ref _uiMotionDurationMilliseconds, previous, nameof(UiMotionDurationMilliseconds)));
        }
    }

    public bool AppearanceBackgroundImageEnabled
    {
        get => _appearanceBackgroundImageEnabled;
        set
        {
            var previous = _appearanceBackgroundImageEnabled;
            if (!SetProperty(ref _appearanceBackgroundImageEnabled, value))
            {
                return;
            }

            if (!TryPersistSetting(
                    nameof(AppearanceBackgroundImageEnabled),
                    settings => settings.AppearanceBackgroundImageEnabled = value,
                    () =>
                    {
                        SetProperty(ref _appearanceBackgroundImageEnabled, previous, nameof(AppearanceBackgroundImageEnabled));
                        NotifyAppearanceBackgroundPropertiesChanged();
                    }))
            {
                return;
            }

            NotifyAppearanceBackgroundPropertiesChanged();
            SetAppearanceStatus(value ? "Status.AppearanceBackgroundEnabled" : "Status.AppearanceBackgroundDisabled");
        }
    }

    public string AppearanceBackgroundImagePath
    {
        get => _appearanceBackgroundImagePath;
        private set
        {
            if (SetProperty(ref _appearanceBackgroundImagePath, value))
            {
                OnPropertyChanged(nameof(AppearanceBackgroundImageDisplayPath));
                OnPropertyChanged(nameof(AppearanceBackgroundImageVisibility));
                if (ClearAppearanceBackgroundCommand is RelayCommand relayCommand)
                {
                    relayCommand.RaiseCanExecuteChanged();
                }
            }
        }
    }

    public string AppearanceBackgroundImageDisplayPath =>
        string.IsNullOrWhiteSpace(AppearanceBackgroundImagePath)
            ? T("Status.AppearanceNoBackgroundImage")
            : AppearanceBackgroundImagePath;

    public string SelectedAppearanceBackgroundMode
    {
        get => _selectedAppearanceBackgroundMode;
        set
        {
            var normalized = JsonAppSettingsStore.NormalizeAppearanceBackgroundMode(value);
            var previous = _selectedAppearanceBackgroundMode;
            if (!SetProperty(ref _selectedAppearanceBackgroundMode, normalized))
            {
                return;
            }

            if (!TryPersistSetting(
                    nameof(SelectedAppearanceBackgroundMode),
                    settings => settings.AppearanceBackgroundMode = normalized,
                    () =>
                    {
                        SetProperty(ref _selectedAppearanceBackgroundMode, previous, nameof(SelectedAppearanceBackgroundMode));
                        NotifyAppearanceBackgroundPropertiesChanged();
                    }))
            {
                return;
            }

            NotifyAppearanceBackgroundPropertiesChanged();
            SetAppearanceStatus(normalized == "split" ? "Status.AppearanceBackgroundModeSplit" : "Status.AppearanceBackgroundModeUnified");
        }
    }

    public string AppearanceSidebarBackgroundImagePath
    {
        get => _appearanceSidebarBackgroundImagePath;
        private set
        {
            if (SetProperty(ref _appearanceSidebarBackgroundImagePath, value))
            {
                NotifyAppearanceBackgroundPropertiesChanged();
            }
        }
    }

    public string AppearanceSidebarBackgroundPreviewPath =>
        string.IsNullOrWhiteSpace(_pendingAppearanceSidebarBackgroundImagePath)
            ? AppearanceSidebarBackgroundImagePath
            : _pendingAppearanceSidebarBackgroundImagePath;

    public bool HasPendingAppearanceSidebarBackground => !string.IsNullOrWhiteSpace(_pendingAppearanceSidebarBackgroundImagePath);

    public Visibility AppearanceSidebarBackgroundPreviewVisibility =>
        AppearanceBackgroundImageEnabled &&
        SelectedAppearanceBackgroundMode == "split" &&
        !string.IsNullOrWhiteSpace(AppearanceSidebarBackgroundPreviewPath)
            ? Visibility.Visible
            : Visibility.Collapsed;

    public Visibility AppearanceSidebarAdjustmentVisibility =>
        HasPendingAppearanceSidebarBackground ? Visibility.Visible : Visibility.Collapsed;

    public string AppearanceContentBackgroundImagePath
    {
        get => _appearanceContentBackgroundImagePath;
        private set
        {
            if (SetProperty(ref _appearanceContentBackgroundImagePath, value))
            {
                NotifyAppearanceBackgroundPropertiesChanged();
            }
        }
    }

    public string AppearanceContentBackgroundPreviewPath =>
        string.IsNullOrWhiteSpace(_pendingAppearanceContentBackgroundImagePath)
            ? AppearanceContentBackgroundImagePath
            : _pendingAppearanceContentBackgroundImagePath;

    public bool HasPendingAppearanceContentBackground => !string.IsNullOrWhiteSpace(_pendingAppearanceContentBackgroundImagePath);

    public Visibility AppearanceContentBackgroundPreviewVisibility =>
        AppearanceBackgroundImageEnabled &&
        SelectedAppearanceBackgroundMode == "split" &&
        !string.IsNullOrWhiteSpace(AppearanceContentBackgroundPreviewPath)
            ? Visibility.Visible
            : Visibility.Collapsed;

    public Visibility AppearanceContentAdjustmentVisibility =>
        HasPendingAppearanceContentBackground ? Visibility.Visible : Visibility.Collapsed;

    public string AppearanceSplitBackgroundSummary =>
        F(
            "Status.AppearanceSplitBackgroundSummaryFormat",
            string.IsNullOrWhiteSpace(AppearanceSidebarBackgroundImagePath) ? T("Status.AppearanceNoBackgroundImage") : AppearanceSidebarBackgroundImagePath,
            string.IsNullOrWhiteSpace(AppearanceContentBackgroundImagePath) ? T("Status.AppearanceNoBackgroundImage") : AppearanceContentBackgroundImagePath);

    public double AppearanceBackgroundImageOpacity
    {
        get => _appearanceBackgroundImageOpacity;
        set
        {
            var normalized = JsonAppSettingsStore.NormalizeAppearanceBackgroundImageOpacity(value);
            var previous = _appearanceBackgroundImageOpacity;
            if (!SetProperty(ref _appearanceBackgroundImageOpacity, normalized))
            {
                return;
            }

            TryPersistSetting(
                nameof(AppearanceBackgroundImageOpacity),
                settings => settings.AppearanceBackgroundImageOpacity = normalized,
                () => SetProperty(ref _appearanceBackgroundImageOpacity, previous, nameof(AppearanceBackgroundImageOpacity)));
        }
    }

    public string SelectedAppearanceThemeKey
    {
        get => _selectedAppearanceThemeKey;
        set
        {
            var normalized = JsonAppSettingsStore.NormalizeAppearanceThemeKey(value);
            var previous = _selectedAppearanceThemeKey;
            if (!SetProperty(ref _selectedAppearanceThemeKey, normalized))
            {
                return;
            }

            if (!TryPersistSetting(
                    nameof(SelectedAppearanceThemeKey),
                    settings => settings.AppearanceThemeKey = normalized,
                    () => SetProperty(ref _selectedAppearanceThemeKey, previous, nameof(SelectedAppearanceThemeKey))))
            {
                return;
            }

            ApplyAppearanceResources();
            SetAppearanceStatus("Status.AppearanceThemeApplied", GetAppearanceThemeDisplayName(normalized));
        }
    }

    public string AppearanceSurfaceColorHex
    {
        get => _appearanceSurfaceColorHex;
        set
        {
            var normalized = JsonAppSettingsStore.NormalizeAppearanceColorHex(value);
            var previous = _appearanceSurfaceColorHex;
            if (!SetProperty(ref _appearanceSurfaceColorHex, normalized))
            {
                return;
            }

            if (!TryPersistSetting(
                    nameof(AppearanceSurfaceColorHex),
                    settings => settings.AppearanceSurfaceColorHex = normalized,
                    () => SetProperty(ref _appearanceSurfaceColorHex, previous, nameof(AppearanceSurfaceColorHex))))
            {
                return;
            }

            ApplyAppearanceResources();
            SetAppearanceStatus(string.IsNullOrWhiteSpace(normalized) ? "Status.AppearanceSurfaceColorReset" : "Status.AppearanceSurfaceColorApplied", normalized);
        }
    }

    public string AppearanceBorderColorHex
    {
        get => _appearanceBorderColorHex;
        set
        {
            var normalized = JsonAppSettingsStore.NormalizeAppearanceColorHex(value);
            var previous = _appearanceBorderColorHex;
            if (!SetProperty(ref _appearanceBorderColorHex, normalized))
            {
                return;
            }

            if (!TryPersistSetting(
                    nameof(AppearanceBorderColorHex),
                    settings => settings.AppearanceBorderColorHex = normalized,
                    () => SetProperty(ref _appearanceBorderColorHex, previous, nameof(AppearanceBorderColorHex))))
            {
                return;
            }

            ApplyAppearanceResources();
            SetAppearanceStatus(string.IsNullOrWhiteSpace(normalized) ? "Status.AppearanceBorderColorReset" : "Status.AppearanceBorderColorApplied", normalized);
        }
    }

    public double AppearanceWindowOverlayOpacity
    {
        get => _appearanceWindowOverlayOpacity;
        set
        {
            var normalized = JsonAppSettingsStore.NormalizeAppearanceWindowOverlayOpacity(value);
            var previous = _appearanceWindowOverlayOpacity;
            if (!SetProperty(ref _appearanceWindowOverlayOpacity, normalized))
            {
                return;
            }

            if (!TryPersistSetting(
                    nameof(AppearanceWindowOverlayOpacity),
                    settings => settings.AppearanceWindowOverlayOpacity = normalized,
                    () => SetProperty(ref _appearanceWindowOverlayOpacity, previous, nameof(AppearanceWindowOverlayOpacity))))
            {
                return;
            }

            ApplyAppearanceResources();
        }
    }

    public double AppearanceCardOpacity
    {
        get => _appearanceCardOpacity;
        set
        {
            var normalized = JsonAppSettingsStore.NormalizeAppearanceCardOpacity(value);
            var previous = _appearanceCardOpacity;
            if (!SetProperty(ref _appearanceCardOpacity, normalized))
            {
                return;
            }

            if (!TryPersistSetting(
                    nameof(AppearanceCardOpacity),
                    settings => settings.AppearanceCardOpacity = normalized,
                    () => SetProperty(ref _appearanceCardOpacity, previous, nameof(AppearanceCardOpacity))))
            {
                return;
            }

            ApplyAppearanceResources();
        }
    }

    public SolidColorBrush AppearanceWindowOverlayBrush
    {
        get => _appearanceWindowOverlayBrush;
        private set => SetProperty(ref _appearanceWindowOverlayBrush, value);
    }

    public SolidColorBrush AppearancePreviewOverlayBrush
    {
        get => _appearancePreviewOverlayBrush;
        private set => SetProperty(ref _appearancePreviewOverlayBrush, value);
    }

    public SolidColorBrush AppearanceSidebarOverlayBrush
    {
        get => _appearanceSidebarOverlayBrush;
        private set => SetProperty(ref _appearanceSidebarOverlayBrush, value);
    }

    public SolidColorBrush AppearanceContentOverlayBrush
    {
        get => _appearanceContentOverlayBrush;
        private set => SetProperty(ref _appearanceContentOverlayBrush, value);
    }

    public Visibility AppearanceBackgroundImageVisibility =>
        AppearanceBackgroundImageEnabled &&
        SelectedAppearanceBackgroundMode == "unified" &&
        !string.IsNullOrWhiteSpace(AppearanceBackgroundImagePath)
            ? Visibility.Visible
            : Visibility.Collapsed;

    public Visibility AppearanceSidebarBackgroundImageVisibility =>
        AppearanceBackgroundImageEnabled &&
        SelectedAppearanceBackgroundMode == "split" &&
        !string.IsNullOrWhiteSpace(AppearanceSidebarBackgroundImagePath)
            ? Visibility.Visible
            : Visibility.Collapsed;

    public Visibility AppearanceContentBackgroundImageVisibility =>
        AppearanceBackgroundImageEnabled &&
        SelectedAppearanceBackgroundMode == "split" &&
        !string.IsNullOrWhiteSpace(AppearanceContentBackgroundImagePath)
            ? Visibility.Visible
            : Visibility.Collapsed;

    public string AppearanceStatus
    {
        get => _appearanceStatus;
        private set => SetProperty(ref _appearanceStatus, value);
    }

    public bool IsSchedulingSuspended
    {
        get => _isSchedulingSuspended;
        private set
        {
            if (SetProperty(ref _isSchedulingSuspended, value) &&
                ResumeSchedulingCommand is RelayCommand relayCommand)
            {
                relayCommand.RaiseCanExecuteChanged();
                UpdateMonitoringInteractionState();
            }
        }
    }

    public bool SupportsGpuClockLimit
    {
        get => _supportsGpuClockLimit;
        private set => SetProperty(ref _supportsGpuClockLimit, value);
    }

    public bool SupportsGpuVoltageControl
    {
        get => _supportsGpuVoltageControl;
        private set => SetProperty(ref _supportsGpuVoltageControl, value);
    }

    public bool SupportsGpuVendorExtensions
    {
        get => _supportsGpuVendorExtensions;
        private set => SetProperty(ref _supportsGpuVendorExtensions, value);
    }

    public bool SupportsEfficiencyModeHint
    {
        get => _supportsEfficiencyModeHint;
        private set => SetProperty(ref _supportsEfficiencyModeHint, value);
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        ReloadRecentLogs();
        await LoadRuntimeHistoryAsync(cancellationToken);
        await LoadTelemetryHistoryAsync(cancellationToken);
        await LoadPowerPlansAsync(cancellationToken);
        await LoadLocalLibraryAsync(cancellationToken);
        await LoadCommunityProfilesAsync(cancellationToken);
        await RefreshCapabilitiesAsync(cancellationToken);
        await RefreshDeviceFingerprintAsync(cancellationToken);
        if (!IsSchedulingSuspended &&
            _startupRecoverySafetyGate.CanResume &&
            _configurationRecoverySafetyGate.CanResume &&
            AutoStartMonitoringOnLaunch)
        {
            await StartMonitoringAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _monitoringLifecycleGate.WaitAsync();
        try
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;
            await StopMonitoringCoreAsync();
        }
        finally
        {
            _monitoringLifecycleGate.Release();
        }

        var profileHistoryCts = _profileHistoryCts;
        _profileHistoryCts = null;
        profileHistoryCts?.Cancel();
        _foregroundChangeNotifier.ForegroundChanged -= OnForegroundChanged;
        _foregroundChangeNotifier.Dispose();
        _logger.EntryWritten -= OnEntryWritten;
        _localizationService.LanguageChanged -= OnLanguageChanged;
        ProfileDraft.ErrorsChanged -= OnProfileDraftErrorsChanged;
        Strings.Dispose();
    }

    private async Task RefreshAsync(bool announceResult = true, bool updateMonitoringStatus = true)
    {
        if (_isDisposed ||
            IsSchedulingSuspended ||
            !_startupRecoverySafetyGate.CanResume ||
            !_configurationRecoverySafetyGate.CanResume)
        {
            return;
        }

        await _refreshGate.WaitAsync();
        try
        {
            var result = await _schedulerOrchestrator.EvaluateForegroundAsync();
            if (_isDisposed)
            {
                return;
            }

            if (_trackAppliedProfileAsync is not null)
            {
                var trackedProfile = result.Success || result.FailureRecoveryCompleted == false
                    ? result.MatchResult?.Profile
                    : null;
                try
                {
                    await _trackAppliedProfileAsync(trackedProfile, CancellationToken.None);
                }
                catch (StartupRecoveryStateUnavailableException exception)
                {
                    await HandleStartupRecoveryTrackingFailureAsync(
                        exception,
                        "Status.StartupRecoveryStateReadFailed");
                    return;
                }
                catch (Exception exception)
                {
                    await HandleStartupRecoveryTrackingFailureAsync(
                        exception,
                        "Status.StartupRecoveryStateWriteFailed");
                    return;
                }
            }

            _lastRunResult = result;
            UpdateFromResult(result);
            AddRuntimeHistoryResult(result);

            if (announceResult)
            {
                AnnounceForegroundEvaluation(result, updateMonitoringStatus);
            }
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private async Task ScanNowAsync()
    {
        if (_isDisposed ||
            IsSchedulingSuspended ||
            !_startupRecoverySafetyGate.CanResume ||
            !_configurationRecoverySafetyGate.CanResume)
        {
            SetMonitoringStatus(
                !_startupRecoverySafetyGate.CanResume
                    ? "Status.ResumeBlockedByStartupRecovery"
                    : !_configurationRecoverySafetyGate.CanResume
                        ? "Status.ResumeBlockedByConfigurationRecovery"
                        : "Status.ScanBlockedWhileSuspended");
            return;
        }

        SetMonitoringStatus("Status.ScanInProgress");
        try
        {
            await RefreshAsync();
        }
        catch (Exception exception)
        {
            _logger.Error("Manual foreground scan failed.", exception);
            SetMonitoringStatus("Status.ScanFailed", exception.Message);
        }
    }

    private async Task ToggleMonitoringAsync()
    {
        if (IsSchedulingSuspended)
        {
            if (!ResumeScheduling())
            {
                return;
            }

            await StartMonitoringAsync();
            return;
        }

        if (IsMonitoringRunning)
        {
            await StopMonitoringAsync();
            return;
        }

        await StartMonitoringAsync();
    }

    public void ApplyStartupRecoveryResult(StartupRecoveryResult result)
    {
        _startupRecoverySafetyGate.Apply(result);
        IsSchedulingSuspended = result.SchedulingSuspended ||
                                _startupRecoverySafetyGate.RequiresManualAction ||
                                _configurationRecoverySafetyGate.RequiresManualAction;
        UpdateMonitoringInteractionState();

        if (_configurationRecoverySafetyGate.RequiresManualAction)
        {
            SetMonitoringStatus("Status.ConfigurationRecoveryRequired");
        }

        if (!result.HasNotice)
        {
            return;
        }

        if (result.StartupStateReadFailed)
        {
            SetMonitoringStatus("Status.StartupRecoveryStateReadFailed");
            return;
        }

        if (result.FailureStateReadFailed)
        {
            SetMonitoringStatus("Status.StartupRecoveryFailureStateReadFailed");
            return;
        }

        if (result.GpuRecoveryFailed)
        {
            SetMonitoringStatus("Status.StartupRecoveryGpuFailed");
            return;
        }

        if (result.RollbackApplied && result.LastProfileDisabled)
        {
            SetMonitoringStatus(
                "Status.StartupRecoveryAppliedAndDisabled",
                result.LastProfileName ?? T("Status.Unknown"),
                result.FailureCount);
            return;
        }

        if (result.RollbackFailed)
        {
            SetMonitoringStatus("Status.StartupRecoveryRollbackFailed");
            return;
        }

        if (result.RollbackPending)
        {
            SetMonitoringStatus("Status.StartupRecoveryRollbackPending");
            return;
        }

        if (result.ProfileDisableFailed)
        {
            SetMonitoringStatus(
                "Status.StartupRecoveryProfileDisableFailed",
                result.LastProfileName ?? T("Status.Unknown"),
                result.FailureCount);
            return;
        }

        if (result.RollbackApplied)
        {
            SetMonitoringStatus("Status.StartupRecoveryApplied");
            return;
        }

        if (result.LastProfileDisabled)
        {
            SetMonitoringStatus(
                "Status.StartupRecoveryDisabledProfile",
                result.LastProfileName ?? T("Status.Unknown"),
                result.FailureCount);
            return;
        }

        SetMonitoringStatus("Status.StartupUnsafeExitDetected");
    }

    private async Task StartMonitoringAsync()
    {
        await _monitoringLifecycleGate.WaitAsync();
        try
        {
            await StartMonitoringCoreAsync();
        }
        finally
        {
            _monitoringLifecycleGate.Release();
        }
    }

    private async Task StartMonitoringCoreAsync()
    {
        if (_isDisposed ||
            IsSchedulingSuspended ||
            !_startupRecoverySafetyGate.CanResume ||
            !_configurationRecoverySafetyGate.CanResume)
        {
            var statusKey = !_startupRecoverySafetyGate.CanResume
                ? "Status.ResumeBlockedByStartupRecovery"
                : !_configurationRecoverySafetyGate.CanResume
                    ? "Status.ResumeBlockedByConfigurationRecovery"
                    : "Status.MonitorBlockedWhileSuspended";
            SetMonitoringStatus(statusKey);
            return;
        }

        if (_monitoringCts is not null || _monitoringStopRequested)
        {
            return;
        }

        _monitoringStopRequested = false;
        var monitoringCts = new CancellationTokenSource();
        _monitoringCts = monitoringCts;
        if (!_foregroundChangeNotifier.Start())
        {
            _logger.Warn("Foreground change event hook could not be started. Falling back to interval monitoring.");
        }

        _monitoringLoopTask = Task.Run(
            () => MonitorLoopAsync(monitoringCts, monitoringCts.Token));
        UpdateMonitoringInteractionState();
        SetMonitoringStatus("Status.MonitoringActive", MonitoringIntervalSeconds);
        try
        {
            await RefreshAsync();
        }
        catch (Exception exception)
        {
            _logger.Error("Failed to start monitoring.", exception);
            await StopMonitoringCoreAsync();
            SetMonitoringStatus("Status.MonitoringFailed", exception.Message);
        }
    }

    private async Task StopMonitoringAsync()
    {
        await _monitoringLifecycleGate.WaitAsync();
        try
        {
            await StopMonitoringCoreAsync();
            if (!_isDisposed)
            {
                SetMonitoringStatus("Status.MonitoringStopped");
            }
        }
        finally
        {
            _monitoringLifecycleGate.Release();
        }
    }

    private async Task StopMonitoringCoreAsync()
    {
        var monitoringCts = _monitoringCts;
        var monitoringLoopTask = _monitoringLoopTask;
        Task? foregroundEventRefreshTask;
        lock (_foregroundEventSync)
        {
            _monitoringStopRequested = true;
            _foregroundChangeNotifier.Stop();
            monitoringCts?.Cancel();
            foregroundEventRefreshTask = _foregroundEventRefreshTask;
        }

        try
        {
            if (monitoringLoopTask is not null)
            {
                await monitoringLoopTask;
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            await WaitForForegroundEventRefreshAsync(foregroundEventRefreshTask);

            if (ReferenceEquals(_monitoringCts, monitoringCts))
            {
                _monitoringCts = null;
            }

            if (ReferenceEquals(_monitoringLoopTask, monitoringLoopTask))
            {
                _monitoringLoopTask = null;
            }

            monitoringCts?.Dispose();
            if (!_isDisposed)
            {
                _monitoringStopRequested = false;
            }

            UpdateMonitoringInteractionState();
        }
    }

    private async Task RestoreAsync()
    {
        await _monitoringLifecycleGate.WaitAsync();
        try
        {
            await StopMonitoringCoreAsync();

            // Manual scans are not owned by the interval loop, so wait for
            // the shared evaluation gate before restoring the active session.
            await _refreshGate.WaitAsync();
            _refreshGate.Release();

            var schedulerRestored = false;
            try
            {
                schedulerRestored = await _schedulerOrchestrator.RestoreAsync();
            }
            catch (Exception exception)
            {
                _logger.Error("Manual rollback failed; automatic scheduling remains suspended.", exception);
            }

            var gpuRestored = !_startupRecoverySafetyGate.RequiresGpuRecovery;
            try
            {
                if (_recoverGpuAsync is not null)
                {
                    gpuRestored = await _recoverGpuAsync(CancellationToken.None);
                }
            }
            catch (Exception exception)
            {
                gpuRestored = false;
                _logger.Error("Manual GPU recovery failed; automatic scheduling remains suspended.", exception);
            }

            if (schedulerRestored)
            {
                _startupRecoverySafetyGate.MarkRollbackResolved();
            }
            else
            {
                _startupRecoverySafetyGate.MarkRollbackRequired();
            }

            if (gpuRestored)
            {
                _startupRecoverySafetyGate.MarkGpuRecoveryResolved();
            }
            else
            {
                _startupRecoverySafetyGate.MarkGpuRecoveryRequired();
            }

            var restored = schedulerRestored && gpuRestored;
            if (!restored)
            {
                IsSchedulingSuspended = true;
            }

            UpdateMonitoringInteractionState();
            SetMonitoringStatus(restored ? "Status.RollbackApplied" : "Status.RollbackIncomplete");
            await RefreshCapabilitiesAsync();
            await LoadPowerPlansAsync();
        }
        finally
        {
            _monitoringLifecycleGate.Release();
        }
    }

    private async Task HandleStartupRecoveryTrackingFailureAsync(Exception exception, string statusKey)
    {
        _startupRecoverySafetyGate.MarkStateRepairRequired();
        _logger.Error(
            "Startup recovery state could not be persisted after a scheduling evaluation; scheduling is suspended and the active session will be restored.",
            exception);
        IsSchedulingSuspended = true;
        UpdateMonitoringInteractionState();

        try
        {
            await _schedulerOrchestrator.RestoreAsync();
        }
        catch (Exception restoreException)
        {
            _logger.Error(
                "Could not restore the active session after startup recovery state persistence failed.",
                restoreException);
        }

        SetMonitoringStatus(statusKey);
    }

    private void AnnounceForegroundEvaluation(SchedulerRunResult result, bool updateMonitoringStatus)
    {
        if (updateMonitoringStatus)
        {
            var status = GetResultStatus(result);
            SetMonitoringStatus(status.Key, status.Arguments);

            if (result.RuntimeStatePersistenceFailed)
            {
                IsSchedulingSuspended = true;
                UpdateMonitoringInteractionState();
            }
        }
    }

    private async Task MonitorLoopAsync(
        CancellationTokenSource monitoringCts,
        CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(MonitoringIntervalSeconds), cancellationToken);
                var dispatcher = Application.Current?.Dispatcher;
                if (dispatcher is null || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
                {
                    return;
                }

                await dispatcher.InvokeAsync(() => RefreshAsync()).Task.Unwrap();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            _logger.Error("Monitoring loop terminated unexpectedly.", exception);

            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is null || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
            {
                return;
            }

            try
            {
                await dispatcher.InvokeAsync(() =>
                {
                    if (_isDisposed ||
                        _monitoringStopRequested ||
                        !ReferenceEquals(_monitoringCts, monitoringCts))
                    {
                        return;
                    }

                    _monitoringCts = null;
                    _monitoringLoopTask = null;
                    monitoringCts.Dispose();
                    UpdateMonitoringInteractionState();
                    SetMonitoringStatus("Status.MonitoringFailed", exception.Message);
                });
            }
            catch (Exception dispatchException)
            {
                _logger.Error("Monitoring loop failure could not be published to the UI.", dispatchException);
            }
        }
    }

    private void OnForegroundChanged(object? sender, EventArgs e)
    {
        if (!IsMonitoringRunning ||
            _isDisposed ||
            _monitoringStopRequested ||
            IsSchedulingSuspended ||
            !_startupRecoverySafetyGate.CanResume ||
            !_configurationRecoverySafetyGate.CanResume)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (now - _lastForegroundEventAt < TimeSpan.FromMilliseconds(180))
        {
            return;
        }

        _lastForegroundEventAt = now;
        QueueForegroundEventRefresh();
    }

    private void QueueForegroundEventRefresh()
    {
        lock (_foregroundEventSync)
        {
            if (_isDisposed ||
                _monitoringStopRequested ||
                !IsMonitoringRunning ||
                _foregroundEventRefreshQueued)
            {
                return;
            }

            _foregroundEventRefreshQueued = true;
            try
            {
                var dispatcher = Application.Current?.Dispatcher;
                if (dispatcher is null || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
                {
                    _foregroundEventRefreshQueued = false;
                    return;
                }

                var refreshTask = dispatcher.InvokeAsync(RefreshFromForegroundEventAsync).Task.Unwrap();
                _foregroundEventRefreshTask = ObserveForegroundEventRefreshAsync(refreshTask);
            }
            catch (Exception exception)
            {
                _foregroundEventRefreshQueued = false;
                _logger.Error("Foreground event refresh could not be queued.", exception);
            }
        }
    }

    private async Task RunConfigurationImportAsync(Func<Task> importAction)
    {
        ArgumentNullException.ThrowIfNull(importAction);

        await _configurationImportGate.WaitAsync();
        var wasSuspended = IsSchedulingSuspended;
        var operationCompleted = false;
        try
        {
            IsSchedulingSuspended = true;
            UpdateMonitoringInteractionState();

            // Wait for a foreground evaluation already in progress before any
            // configuration file can be replaced.
            await _refreshGate.WaitAsync();
            _refreshGate.Release();

            await importAction();
            operationCompleted = true;
        }
        catch (Exception exception)
        {
            if (TryGetConfigurationRecoveryFailure(exception, out _))
            {
                MarkConfigurationRecoveryRequired();
                IsSchedulingSuspended = true;
            }

            throw;
        }
        finally
        {
            if (operationCompleted)
            {
                IsSchedulingSuspended = wasSuspended ||
                                        !_startupRecoverySafetyGate.CanResume ||
                                        !_configurationRecoverySafetyGate.CanResume;
            }
            else if (!_configurationRecoverySafetyGate.RequiresManualAction)
            {
                IsSchedulingSuspended = wasSuspended;
            }

            UpdateMonitoringInteractionState();
            _configurationImportGate.Release();
        }
    }

    private async Task ResolveConfigurationRecoveryAsync()
    {
        if (!_configurationRecoverySafetyGate.RequiresManualAction)
        {
            return;
        }

        var confirmation = MessageBox.Show(
            T("Dialog.ConfigurationRecoveryResolveQuestion"),
            T("Dialog.ConfigurationRecoveryResolveTitle"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (confirmation != MessageBoxResult.Yes)
        {
            return;
        }

        await _configurationImportGate.WaitAsync();
        try
        {
            IsSchedulingSuspended = true;
            UpdateMonitoringInteractionState();

            // Serialize recovery validation with imports and wait for any
            // foreground evaluation to finish before reading replacement files.
            await _refreshGate.WaitAsync();
            _refreshGate.Release();

            _settingsStore.ReloadFromDisk();
            LoadSettings();
            NotifySettingsChanged();
            await LoadLocalLibraryAsync();
            var loadIssueCount = new object[] { _profileManager, _deviceStrategyPackageStore, _settingsStore }
                .OfType<IConfigurationLoadIssueSource>()
                .Sum(source => source.LoadIssues.Count);
            if (loadIssueCount > 0)
            {
                SetMonitoringStatus("Status.ConfigurationRecoveryStillRequired", loadIssueCount);
                return;
            }

            var libraryValidation = ProfileSafetyValidator.EvaluateLibrary(
                Profiles.Select(item => item.Profile));
            if (!libraryValidation.CanPersist)
            {
                var safetyErrors = string.Join(
                    Environment.NewLine,
                    libraryValidation.Errors.Select(GetProfileSafetyIssueText));
                SetMonitoringStatus("Status.ConfigurationRecoveryValidationFailed", safetyErrors);
                return;
            }

            if (!_configurationRecoveryMarkerStore.TryClear(out var clearFailure))
            {
                _logger.Error(
                    "Configuration recovery was validated, but the recovery marker could not be cleared.",
                    clearFailure ?? new IOException("Unknown marker cleanup failure."));
                SetMonitoringStatus("Status.ConfigurationRecoveryClearFailed");
                return;
            }

            _configurationRecoverySafetyGate.MarkRecoveryResolved();
            IsSchedulingSuspended = !_startupRecoverySafetyGate.CanResume;
            UpdateMonitoringInteractionState();
            SetMonitoringStatus("Status.ConfigurationRecoveryResolved");
        }
        finally
        {
            _configurationImportGate.Release();
        }
    }

    private async Task ObserveForegroundEventRefreshAsync(Task refreshTask)
    {
        try
        {
            await refreshTask;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            _logger.Error("Foreground event refresh task terminated unexpectedly.", exception);
        }
        finally
        {
            lock (_foregroundEventSync)
            {
                _foregroundEventRefreshQueued = false;
            }
        }
    }

    private async Task RefreshFromForegroundEventAsync()
    {
        try
        {
            if (!IsMonitoringRunning ||
                _isDisposed ||
                _monitoringStopRequested ||
                IsSchedulingSuspended)
            {
                return;
            }

            await RefreshAsync();
        }
        catch (Exception exception)
        {
            _logger.Error("Foreground event refresh failed.", exception);
            SetMonitoringStatus("Status.ScanFailed", exception.Message);
        }
    }

    private static async Task WaitForForegroundEventRefreshAsync(Task? refreshTask)
    {
        if (refreshTask is null)
        {
            return;
        }

        try
        {
            await refreshTask;
        }
        catch (Exception)
        {
            // ObserveForegroundEventRefreshAsync already logs failures. The
            // extra guard keeps shutdown from being blocked by a UI callback.
        }
    }

    private async Task RefreshCapabilitiesAsync(CancellationToken cancellationToken = default)
    {
        CapabilitySnapshot capabilities;
        try
        {
            capabilities = await _schedulerOrchestrator.GetCapabilitiesAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.Error("Capability detection could not be refreshed.", exception);
            _capabilityRefreshFailed = true;
            CapabilityLines.Clear();
            CapabilityLines.Add(T("Capability.DetectionFailed"));
            return;
        }

        _capabilityRefreshFailed = false;
        _capabilitySnapshot = capabilities;
        ApplyCapabilitySnapshot(capabilities);
        await RefreshTelemetryCapabilitiesAsync(cancellationToken);
    }

    private void ApplyCapabilitySnapshot(CapabilitySnapshot capabilities)
    {
        CapabilityLines.Clear();
        CapabilityLines.Add(capabilities.SupportsPowerPlanSwitching
            ? F("Capability.PowerPlanSupported", capabilities.AvailablePowerPlans.Count)
            : T("Capability.PowerPlanUnsupported"));
        CapabilityLines.Add(capabilities.SupportsPriorityBoost
            ? T("Capability.PrioritySupported")
            : T("Capability.PriorityUnsupported"));
        CapabilityLines.Add(capabilities.SupportsRollback
            ? T("Capability.RollbackSupported")
            : T("Capability.RollbackUnsupported"));
        CapabilityLines.Add(capabilities.SupportsMetricsCollection
            ? T("Capability.MetricsSupported")
            : T("Capability.MetricsUnsupported"));
        CapabilityLines.Add(capabilities.SupportsEfficiencyModeHint
            ? T("Capability.EfficiencySupported")
            : T("Capability.EfficiencyUnsupported"));
        CapabilityLines.Add(F(
            capabilities.Gpu.StatusKey,
            capabilities.Gpu.AdapterName,
            GetGpuVendorLabel(capabilities.Gpu.Vendor),
            capabilities.Gpu.ProviderName));

        foreach (var reason in capabilities.UnsupportedReasons)
        {
            var localizedReason = reason.StartsWith("Capability.", StringComparison.OrdinalIgnoreCase)
                ? T(reason)
                : reason;
            CapabilityLines.Add(F("Capability.NoticeFormat", localizedReason));
        }

        if (capabilities.AvailablePowerPlans.Count > 0)
        {
            CapabilityLines.Add(F("Capability.DetectedPlans", string.Join(", ", capabilities.AvailablePowerPlans.Select(plan => plan.Name))));
        }

        SupportsGpuVendorExtensions = capabilities.Gpu.SupportsVendorExtensions;
        SupportsEfficiencyModeHint = capabilities.SupportsEfficiencyModeHint;
        SupportsGpuClockLimit = capabilities.Gpu.SupportsClockLimit;
        SupportsGpuVoltageControl = capabilities.Gpu.SupportsVoltageControl;
        GpuAdapterSummary = capabilities.Gpu.HasDetectedAdapter
            ? F("Status.GpuAdapterSummaryFormat", capabilities.Gpu.AdapterName, GetGpuVendorLabel(capabilities.Gpu.Vendor), capabilities.Gpu.ProviderName)
            : T("Status.GpuAdapterUnavailable");
        GpuCapabilitySummary = F(
            "Status.GpuCapabilitySummaryFormat",
            SupportsGpuVendorExtensions ? T("Common.Yes") : T("Common.No"),
            capabilities.Gpu.SupportsApplyPipeline ? T("Common.Yes") : T("Common.No"),
            SupportsGpuClockLimit ? T("Common.Yes") : T("Common.No"),
            SupportsGpuVoltageControl ? T("Common.Yes") : T("Common.No"));
        GpuControlReadinessSummary = F(
            "Status.GpuControlReadinessFormat",
            T($"Enum.GpuControlReadinessLevel.{capabilities.GpuControl.Level}"),
            capabilities.GpuControl.SupportsReversibleLimits ? T("Common.Yes") : T("Common.No"),
            T(capabilities.GpuControl.ReasonKey));
    }

    private async Task RefreshCapabilitiesFromCommandAsync()
    {
        _schedulerOrchestrator.InvalidateCapabilities();
        _telemetryObservationService.InvalidateProviderCapabilities();
        await RefreshCapabilitiesAsync();
    }

    private void UpdateFromResult(SchedulerRunResult result)
    {
        var activeApp = result.ActiveApp;
        FocusedAppName = activeApp?.DisplayName ?? T("Status.ForegroundAppUnavailable");
        FocusedAppDetails = activeApp is null
            ? T("Status.NoActiveProcessMetadata")
            : F("Status.ForegroundDetailsFormat", activeApp.ProcessName, activeApp.ProcessId, activeApp.ExecutablePath ?? T("Status.PathUnavailable"));
        ClassificationSummary = activeApp is null
            ? F("Status.ClassificationFormat", T("Enum.ProcessClassification.Unknown"))
            : F("Status.ClassificationAndPowerFormat", GetClassificationLabel(activeApp.Classification), GetPowerSourceLabel(activeApp.PowerSourceMode));
        MatchedProfileName = result.MatchResult?.Profile.Name ?? T("Status.NoProfileMatched");
        ProfileReason = result.MatchResult is null
            ? T("Status.CurrentForegroundNoMatch")
            : F("Status.ProfileScoreFormat", result.MatchResult.Score, result.MatchResult.Profile.Version);
        RefreshProfileMatchReasons(result.MatchResult);
        ActivePowerPlanName = result.ActivePowerPlan?.Name ?? T("Status.NoActivePowerPlan");
        PrioritySummary = result.PriorityChange is null
            ? T("Status.NoPriorityChange")
            : F(
                "Status.PrioritySummaryFormat",
                result.PriorityChange.Message,
                result.PriorityChange.PreviousPriority is { } priority
                    ? GetPriorityLabel(priority)
                    : T("Status.PreviousUnknown"));
        MetricsSummary = result.Metrics is null || !result.Metrics.IsAvailable
            ? T("Status.NoMetricsYet")
            : F(
                "Status.MetricsSummaryFormat",
                result.Metrics.CpuUsagePercent is { } cpuUsage ? $"{cpuUsage:F1}%" : "--",
                result.Metrics.WorkingSetMb,
                result.Metrics.ThreadCount);
        FocusedAppCpuUsagePercent = result.Metrics?.CpuUsagePercent;
        CurrentPowerSourceLabel = activeApp is not null
            ? GetPowerSourceLabel(activeApp.PowerSourceMode)
            : T("Status.Unknown");
        LastSummary = BuildLocalizedResultSummary(result);
        LastRefreshTime = F("Status.LastScanFormat", result.OccurredAt.LocalDateTime);
        RefreshCurrentAppProfilesState();
    }

    private string BuildLocalizedResultSummary(SchedulerRunResult result)
    {
        var status = GetResultStatus(result);
        return F(status.Key, status.Arguments);
    }

    private (string Key, object[] Arguments) GetResultStatus(SchedulerRunResult result)
    {
        var profileName = result.MatchResult?.Profile.Name ?? T("Status.Unknown");
        if (result.RuntimeStatePersistenceFailed)
        {
            return result.FailureRecoveryCompleted == false
                ? ("Status.SummaryRuntimeStatePersistenceRecoveryPending", Array.Empty<object>())
                : ("Status.SummaryRuntimeStatePersistenceFailed", Array.Empty<object>());
        }

        if (result.IsRetryDeferred && result.RetryAfter is { } retryAfter)
        {
            return (
                "Status.SummaryRetryDeferred",
                [profileName, result.ConsecutiveFailureCount, retryAfter.LocalDateTime]);
        }

        if (!result.Success && result.MatchResult is not null)
        {
            if (result.FailureRecoveryCompleted == false && result.Actions.Count == 0)
            {
                return ("Status.SummaryRecoveryBlocked", [profileName]);
            }

            var failedActionCount = result.Actions.Count(action => action.Status == SchedulerActionStatus.Failed);
            if (result.FailureRecoveryCompleted == false)
            {
                return ("Status.SummaryProfileFailedRecoveryPending", [profileName, failedActionCount]);
            }

            return result.RetryAfter is { } nextRetry
                ? (
                    "Status.SummaryProfileFailedRecovered",
                    [profileName, failedActionCount, result.ConsecutiveFailureCount, nextRetry.LocalDateTime])
                : ("Status.SummaryEvaluationFailed", Array.Empty<object>());
        }

        if (!result.Success)
        {
            return ("Status.SummaryEvaluationFailed", Array.Empty<object>());
        }

        if (result.MatchResult is null)
        {
            return ("Status.SummaryNoMatch", [result.ActiveApp?.ProcessName ?? T("Status.Unknown")]);
        }

        var backgroundCount = result.BackgroundAdjustments.Count(item => item.Status == SchedulerActionStatus.Applied);
        var unsupportedCount = SchedulerResultStatusEvaluator.CountUnsupportedActions(result.Actions);
        if (unsupportedCount > 0)
        {
            return backgroundCount == 0
                ? (
                    "Status.SummaryPartiallyAppliedProfile",
                    [profileName, result.ActiveApp?.ProcessName ?? T("Status.Unknown"), unsupportedCount])
                : (
                    "Status.SummaryPartiallyAppliedProfileWithBackground",
                    [profileName, result.ActiveApp?.ProcessName ?? T("Status.Unknown"), backgroundCount, unsupportedCount]);
        }

        return backgroundCount == 0
            ? (
                "Status.SummaryAppliedProfile",
                [profileName, result.ActiveApp?.ProcessName ?? T("Status.Unknown")])
            : (
                "Status.SummaryAppliedProfileWithBackground",
                [profileName, result.ActiveApp?.ProcessName ?? T("Status.Unknown"), backgroundCount]);
    }

    private void ReloadRecentLogs()
    {
        RecentLogs.Clear();
        foreach (var entry in _logger.GetRecentEntries().Reverse())
        {
            RecentLogs.Add(CreateLogEntryItem(entry));
        }
    }

    private void RefreshStorageSummary()
    {
        StorageSummary = F(
            "Status.StorageSummaryFormat",
            Profiles.Count,
            DeviceStrategyPackages.Count,
            _profilesDirectory,
            _deviceStrategiesDirectory,
            _communityCatalogPath,
            _databasePath);
        DataLocationSummary = F(
            "Status.DataLocationSummaryFormat",
            _applicationDirectory,
            _profilesDirectory,
            _deviceStrategiesDirectory,
            _settingsPath,
            _runtimeDirectory,
            _communityCatalogPath,
            _databasePath,
            _localesDirectory,
            _logsDirectory);
    }

    private void OpenDirectory(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{directory}\"",
                UseShellExecute = true
            });
        }
        catch (Exception exception)
        {
            _logger.Error($"Failed to open directory: {directory}", exception);
            SetMonitoringStatus("Status.OpenDirectoryFailed", directory, exception.Message);
        }
    }

    private async Task LoadProfileHistoryAsync(string profileId)
    {
        var cancellationTokenSource = new CancellationTokenSource();
        var previousCancellationTokenSource = _profileHistoryCts;
        _profileHistoryCts = cancellationTokenSource;
        previousCancellationTokenSource?.Cancel();

        try
        {
            var revisions = await _profileManager.LoadProfileHistoryAsync(
                profileId,
                cancellationTokenSource.Token);
            cancellationTokenSource.Token.ThrowIfCancellationRequested();
            if (_isDisposed ||
                !string.Equals(SelectedProfile?.Profile.Id, profileId, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            foreach (var revision in revisions)
            {
                ProfileHistory.Add(revision);
            }

            SelectedProfileRevision = ProfileHistory.FirstOrDefault();
        }
        catch (OperationCanceledException) when (cancellationTokenSource.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _logger.Warn($"Profile history could not be loaded for '{profileId}'. {exception.Message}");
        }
        finally
        {
            if (ReferenceEquals(_profileHistoryCts, cancellationTokenSource))
            {
                _profileHistoryCts = null;
            }

            cancellationTokenSource.Dispose();
        }
    }

    private void ApplyValidationIssues(IReadOnlyList<string> issues)
    {
        ValidationIssues.Clear();
        foreach (var issue in issues)
        {
            ValidationIssues.Add(issue);
        }

        if (issues.Count == 0)
        {
            SetValidationSummary("Validation.Passed");
        }
        else
        {
            SetValidationSummary("Validation.FoundCount", issues.Count);
        }
    }

    private void ClearValidationIssues()
    {
        ValidationIssues.Clear();
        SetValidationSummary("Status.ValidationReady");
    }

    private void SetMonitoringStatus(string key, params object[] args)
    {
        if (_isDisposed)
        {
            return;
        }

        _monitoringStatusKey = key;
        _monitoringStatusArgs = args;
        ApplyMonitoringStatus();

        if (ShouldPublishMonitoringNotice(key))
        {
            AddStatusCenterEvent("Section.RuntimeStatus", key, args, GetStatusCenterLevel(key));
        }
    }

    private async Task HandleCommandException(Exception exception)
    {
        _logger.Error("UI command failed.", exception);

        if (TryGetConfigurationRecoveryFailure(exception, out var recoveryFailureCount))
        {
            MarkConfigurationRecoveryRequired();
            IsSchedulingSuspended = true;
            UpdateMonitoringInteractionState();

            try
            {
                if (!await _schedulerOrchestrator.RestoreAsync())
                {
                    _logger.Error(
                        "Configuration recovery failed and the active scheduling session could not be fully restored.",
                        exception);
                }
            }
            catch (Exception restoreException)
            {
                _logger.Error(
                    "Configuration recovery failed and restoring the active scheduling session raised an exception.",
                    restoreException);
            }

            SetMonitoringStatus("Status.ConfigurationImportRollbackIncomplete", recoveryFailureCount);
            return;
        }

        SetMonitoringStatus("Status.CommandFailed", exception.Message);
    }

    private void MarkConfigurationRecoveryRequired()
    {
        _configurationRecoverySafetyGate.MarkRecoveryRequired();
        try
        {
            _configurationRecoveryMarkerStore.Mark();
        }
        catch (Exception exception)
        {
            // Keep the in-memory gate even if persistence is unavailable; the
            // failure itself is recorded so the next restart can be reviewed.
            _logger.Error("Configuration recovery marker could not be persisted.", exception);
        }
    }

    private static bool TryGetConfigurationRecoveryFailure(Exception exception, out int recoveryFailureCount)
    {
        if (exception is MigrationPackageImportException migrationException)
        {
            if (!migrationException.RollbackSucceeded)
            {
                recoveryFailureCount = migrationException.RecoveryFailureCount;
                return true;
            }

            // The outer migration snapshot is authoritative. A nested store
            // failure is safe to ignore when the complete migration snapshot
            // was restored successfully.
            recoveryFailureCount = 0;
            return false;
        }

        if (exception is IConfigurationRecoveryFailure recoveryFailure)
        {
            if (recoveryFailure.RecoveryFailureCount > 0)
            {
                recoveryFailureCount = recoveryFailure.RecoveryFailureCount;
                return true;
            }

            recoveryFailureCount = 0;
            return false;
        }

        if (exception is AggregateException aggregateException)
        {
            foreach (var innerException in aggregateException.Flatten().InnerExceptions)
            {
                if (TryGetConfigurationRecoveryFailure(innerException, out recoveryFailureCount))
                {
                    return true;
                }
            }
        }

        if (exception.InnerException is not null &&
            TryGetConfigurationRecoveryFailure(exception.InnerException, out recoveryFailureCount))
        {
            return true;
        }

        recoveryFailureCount = 0;
        return false;
    }

    private void ApplyMonitoringStatus()
    {
        MonitoringStatus = FormatByKey(_monitoringStatusKey, _monitoringStatusArgs);
    }

    private void SetProfileEditorStatus(string key, params object[] args)
    {
        _profileEditorStatusKey = key;
        _profileEditorStatusArgs = args;
        ApplyProfileEditorStatus();
        AddStatusCenterEvent("Section.ProfileLibrary", key, args, GetStatusCenterLevel(key));
    }

    private void ApplyProfileEditorStatus()
    {
        ProfileEditorStatus = FormatByKey(_profileEditorStatusKey, _profileEditorStatusArgs);
    }

    private void SetCommunityCatalogStatus(string key, params object[] args)
    {
        _communityCatalogStatusKey = key;
        _communityCatalogStatusArgs = args;
        ApplyCommunityCatalogStatus();
        AddStatusCenterEvent("Section.Community", key, args, GetStatusCenterLevel(key));
    }

    private void ApplyCommunityCatalogStatus()
    {
        CommunityCatalogStatus = FormatByKey(_communityCatalogStatusKey, _communityCatalogStatusArgs);
    }

    private void SetAppearanceStatus(string key, params object[] args)
    {
        _appearanceStatusKey = key;
        _appearanceStatusArgs = args;
        ApplyAppearanceStatus();
    }

    private void ApplyAppearanceStatus()
    {
        AppearanceStatus = FormatByKey(_appearanceStatusKey, _appearanceStatusArgs);
    }

    private void SetValidationSummary(string key, params object[] args)
    {
        _validationSummaryKey = key;
        _validationSummaryArgs = args;
        ApplyValidationSummary();
    }

    private void ApplyValidationSummary()
    {
        ValidationSummary = FormatByKey(_validationSummaryKey, _validationSummaryArgs);
    }

    private void AddStatusCenterEvent(
        string sourceKey,
        string messageKey,
        object[] args,
        string level,
        DateTimeOffset? timestamp = null)
    {
        _statusCenterEvents.Add(new StatusCenterEvent(
            timestamp ?? DateTimeOffset.Now,
            sourceKey,
            messageKey,
            args.ToArray(),
            level));

        if (_statusCenterEvents.Count > 40)
        {
            _statusCenterEvents.RemoveRange(0, _statusCenterEvents.Count - 40);
        }

        RefreshStatusCenterItems();
    }

    private void RefreshStatusCenterItems()
    {
        var refreshedItems = _statusCenterEvents
            .OrderByDescending(static item => item.Timestamp)
            .Select(CreateStatusCenterItem)
            .ToArray();

        for (var index = 0; index < refreshedItems.Length; index++)
        {
            if (index < StatusCenterItems.Count)
            {
                UpdateStatusCenterItem(StatusCenterItems[index], refreshedItems[index]);
            }
            else
            {
                StatusCenterItems.Add(refreshedItems[index]);
            }
        }

        while (StatusCenterItems.Count > refreshedItems.Length)
        {
            StatusCenterItems.RemoveAt(StatusCenterItems.Count - 1);
        }

        StatusCenterSummary = F("Status.StatusCenterSummaryFormat", StatusCenterItems.Count);
        OnPropertyChanged(nameof(StatusCenterCount));
        OnPropertyChanged(nameof(NavigationNoticesSummary));

        if (ClearStatusCenterCommand is RelayCommand relayCommand)
        {
            relayCommand.RaiseCanExecuteChanged();
        }
    }

    private StatusCenterItemViewModel CreateStatusCenterItem(StatusCenterEvent item) =>
        new()
        {
            Timestamp = item.Timestamp,
            Source = T(item.SourceKey),
            Message = FormatByKey(item.MessageKey, item.Args),
            LevelKey = item.Level,
            Level = T($"StatusCenter.Level.{item.Level}")
        };

    private static void UpdateStatusCenterItem(StatusCenterItemViewModel target, StatusCenterItemViewModel source)
    {
        target.Timestamp = source.Timestamp;
        target.Source = source.Source;
        target.Message = source.Message;
        target.LevelKey = source.LevelKey;
        target.Level = source.Level;
    }

    private void ClearStatusCenter()
    {
        _statusCenterEvents.Clear();
        RefreshStatusCenterItems();
    }

    private void ApplyDefaultRunState()
    {
        FocusedAppName = T("Status.NotScannedYet");
        FocusedAppDetails = T("Status.UseScanNow");
        ClassificationSummary = T("Status.ClassificationPending");
        MatchedProfileName = T("Status.NoProfileMatched");
        ProfileReason = T("Status.ProfileWaiting");
        ProfileMatchReasons.Clear();
        ActivePowerPlanName = T("Status.Unknown");
        PrioritySummary = T("Status.NoPriorityChange");
        MetricsSummary = T("Status.NoMetricsYet");
        FocusedAppCpuUsagePercent = null;
        FocusedAppGpuUsagePercent = null;
        FocusedAppFps = null;
        CurrentPowerSourceLabel = T("Status.Unknown");
        GpuAdapterSummary = T("Status.GpuAdapterUnavailable");
        GpuCapabilitySummary = F("Status.GpuCapabilitySummaryFormat", T("Common.No"), T("Common.No"), T("Common.No"), T("Common.No"));
        GpuControlReadinessSummary = F(
            "Status.GpuControlReadinessFormat",
            T("Enum.GpuControlReadinessLevel.NoAdapter"),
            T("Common.No"),
            T("Capability.GpuUnavailable"));
        DeviceFingerprintSummary = T("Status.DeviceFingerprintUnavailable");
        LastSummary = T("Status.FirstFlow");
        LastRefreshTime = T("Status.NoScanTimestamp");
        if (RuntimeHistoryItems.Count == 0)
        {
            ActionAuditItems.Clear();
            ActionAuditSummary = T("Status.ActionAuditEmpty");
            RefreshRuntimeHistoryDetail(null);
        }
    }

    private void ApplyDefaultDraftName()
    {
        if (string.IsNullOrWhiteSpace(ProfileDraft.Name) ||
            string.Equals(ProfileDraft.Name, "New Profile", StringComparison.OrdinalIgnoreCase))
        {
            ProfileDraft.Name = T("Status.DefaultProfileName");
        }
    }

    private void RebuildLocalizedOptionCollections()
    {
        ClassificationOptions.Clear();
        foreach (var value in Enum.GetValues<ProcessClassification>())
        {
            ClassificationOptions.Add(new ValueOptionViewModel<ProcessClassification>
            {
                Value = value,
                DisplayName = GetClassificationLabel(value)
            });
        }

        PriorityOptions.Clear();
        foreach (var value in Enum.GetValues<PriorityLevel>().Where(value => value != PriorityLevel.RealTime))
        {
            PriorityOptions.Add(new ValueOptionViewModel<PriorityLevel>
            {
                Value = value,
                DisplayName = GetPriorityLabel(value)
            });
        }

        IoPriorityOptions.Clear();
        foreach (var value in Enum.GetValues<IoPriorityLevel>())
        {
            IoPriorityOptions.Add(new ValueOptionViewModel<IoPriorityLevel>
            {
                Value = value,
                DisplayName = value.ToString()
            });
        }

        MemoryPriorityOptions.Clear();
        foreach (var value in Enum.GetValues<MemoryPriorityLevel>())
        {
            MemoryPriorityOptions.Add(new ValueOptionViewModel<MemoryPriorityLevel>
            {
                Value = value,
                DisplayName = value.ToString()
            });
        }

        PowerSourceOptions.Clear();
        foreach (var value in Enum.GetValues<PowerSourceMode>())
        {
            PowerSourceOptions.Add(new ValueOptionViewModel<PowerSourceMode>
            {
                Value = value,
                DisplayName = GetPowerSourceLabel(value)
            });
        }

        CommunitySourceFilterOptions.Clear();
        foreach (var value in Enum.GetValues<CommunitySourceFilter>())
        {
            CommunitySourceFilterOptions.Add(new ValueOptionViewModel<CommunitySourceFilter>
            {
                Value = value,
                DisplayName = GetCommunitySourceFilterLabel(value)
            });
        }

        CommunityPowerFilterOptions.Clear();
        CommunityPowerFilterOptions.Add(new ValueOptionViewModel<PowerSourceMode>
        {
            Value = PowerSourceMode.Any,
            DisplayName = T("Common.All")
        });
        foreach (var value in new[] { PowerSourceMode.Ac, PowerSourceMode.Battery })
        {
            CommunityPowerFilterOptions.Add(new ValueOptionViewModel<PowerSourceMode>
            {
                Value = value,
                DisplayName = GetPowerSourceLabel(value)
            });
        }

        CommunityClassificationFilterOptions.Clear();
        CommunityClassificationFilterOptions.Add(new ValueOptionViewModel<ProcessClassification?>
        {
            Value = null,
            DisplayName = T("Common.All")
        });
        foreach (var value in Enum.GetValues<ProcessClassification>().Where(static classification => classification != ProcessClassification.Unknown))
        {
            CommunityClassificationFilterOptions.Add(new ValueOptionViewModel<ProcessClassification?>
            {
                Value = value,
                DisplayName = GetClassificationLabel(value)
            });
        }

        GpuModeOptions.Clear();
        foreach (var value in Enum.GetValues<GpuPreferenceMode>())
        {
            GpuModeOptions.Add(new ValueOptionViewModel<GpuPreferenceMode>
            {
                Value = value,
                DisplayName = GetGpuModeLabel(value)
            });
        }

        TargetExperiencePriorityOptions.Clear();
        foreach (var value in Enum.GetValues<TargetExperiencePriority>())
        {
            TargetExperiencePriorityOptions.Add(new ValueOptionViewModel<TargetExperiencePriority>
            {
                Value = value,
                DisplayName = T($"Enum.TargetExperiencePriority.{value}")
            });
        }

        TargetExperienceControlModeOptions.Clear();
        foreach (var value in Enum.GetValues<TargetExperienceControlMode>().Where(value => value != TargetExperienceControlMode.Automatic))
        {
            TargetExperienceControlModeOptions.Add(new ValueOptionViewModel<TargetExperienceControlMode>
            {
                Value = value,
                DisplayName = T($"Enum.TargetExperienceControlMode.{value}")
            });
        }

        TargetNoisePreferenceOptions.Clear();
        foreach (var value in Enum.GetValues<NoisePreference>())
        {
            TargetNoisePreferenceOptions.Add(new ValueOptionViewModel<NoisePreference>
            {
                Value = value,
                DisplayName = T($"Enum.NoisePreference.{value}")
            });
        }

        BackgroundPolicyCategoryOptions.Clear();
        foreach (var category in new[] { "default", "launcher", "browser", "communication", "media", "productivity", "game", "system", "unknown" })
        {
            BackgroundPolicyCategoryOptions.Add(new ValueOptionViewModel<string>
            {
                Value = category,
                DisplayName = GetCategoryLabel(category)
            });
        }

        MonitoringIntervalOptions.Clear();
        foreach (var value in new[] { 1, 2, 5, 10 })
        {
            MonitoringIntervalOptions.Add(new ValueOptionViewModel<int>
            {
                Value = value,
                DisplayName = F("Option.MonitoringIntervalSeconds", value)
            });
        }

        // Rebuilding localized option objects temporarily clears SelectedValue;
        // notify the bound scalar values after the new items are available.
        OnPropertyChanged(nameof(MonitoringIntervalSeconds));
        OnPropertyChanged(nameof(SelectedCommunitySourceFilter));
        OnPropertyChanged(nameof(SelectedCommunityPowerFilter));
        OnPropertyChanged(nameof(SelectedCommunityClassificationFilter));

        AppearanceThemeOptions.Clear();
        foreach (var theme in AppearanceThemes)
        {
            AppearanceThemeOptions.Add(new ValueOptionViewModel<string>
            {
                Value = theme.Key,
                DisplayName = T(theme.DisplayNameKey)
            });
        }

        OnPropertyChanged(nameof(SelectedAppearanceThemeKey));

        AppearanceBackgroundModeOptions.Clear();
        foreach (var mode in AppearanceBackgroundModes)
        {
            AppearanceBackgroundModeOptions.Add(new ValueOptionViewModel<string>
            {
                Value = mode,
                DisplayName = T($"Appearance.BackgroundMode.{mode}")
            });
        }

        OnPropertyChanged(nameof(SelectedAppearanceBackgroundMode));
    }

    private void RebuildLocalizedViewState()
    {
        ProfileDraft.ConfigureValidation(T, F);
        RebuildLocalizedOptionCollections();
        RebuildProfileItems(Profiles.Select(item => item.Profile).ToArray(), SelectedProfile?.Profile.Id);
        RebuildDeviceStrategyPackageItems(DeviceStrategyPackages.Select(item => item.Package).ToArray(), SelectedDeviceStrategyPackage?.Package.PackageId);
        ApplyCommunityFilter();
        RefreshStorageSummary();
        OnPropertyChanged(nameof(NavigationNoticesSummary));
        OnPropertyChanged(nameof(NavigationProfilesSummary));
        OnPropertyChanged(nameof(NavigationCommunitySummary));
        OnPropertyChanged(nameof(CurrentPageTitle));
        OnPropertyChanged(nameof(CurrentPageDescription));
        OnPropertyChanged(nameof(IsMonitoringRunning));
        OnPropertyChanged(nameof(CanToggleMonitoring));
        OnPropertyChanged(nameof(MonitoringToggleButtonText));
        OnPropertyChanged(nameof(MonitoringModeSummary));
        ApplyMonitoringStatus();
        ApplyProfileEditorStatus();
        ApplyCommunityCatalogStatus();
        ApplyAppearanceStatus();
        ApplyDiagnosticPackageStatus();
        ApplyTelemetryObservationSummary();
        RebuildTelemetryTrendItems();
        RebuildTelemetryHistoryLines();
        RefreshPresentMonConfigurationStatus();
        NotifyAppearanceBackgroundPropertiesChanged();
        RefreshStatusCenterItems();
        ApplyValidationIssues(ProfileDraft.GetValidationMessages());
        OnPropertyChanged(nameof(AppearanceBackgroundImageDisplayPath));
        RelocalizeRecentLogs();
        RelocalizeRuntimeHistory();

        if (_lastRunResult is null)
        {
            ApplyDefaultRunState();
        }
        else
        {
            UpdateFromResult(_lastRunResult);
        }

        _ = LoadPowerPlansAsync();
        _ = RefreshDeviceFingerprintAsync();
    }

    private async Task RefreshDeviceFingerprintAsync(CancellationToken cancellationToken = default)
    {
        DeviceFingerprint device;
        try
        {
            device = await _deviceFingerprintProvider.GetCurrentAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.Error("Device fingerprint could not be refreshed.", exception);
            DeviceFingerprintSummary = T("Status.DeviceFingerprintUnavailable");
            return;
        }

        var memoryText = device.TotalMemoryBytes is { } totalMemoryBytes
            ? F("Status.DeviceMemoryFormat", totalMemoryBytes / 1024d / 1024d / 1024d)
            : T("Status.Unknown");
        var gpuText = device.GpuNames.Count > 0
            ? string.Join(", ", device.GpuNames)
            : T("Status.Unknown");

        DeviceFingerprintSummary = F(
            "Status.DeviceFingerprintFormat",
            string.IsNullOrWhiteSpace(device.Manufacturer) ? T("Status.Unknown") : device.Manufacturer,
            string.IsNullOrWhiteSpace(device.Model) ? T("Status.Unknown") : device.Model,
            string.IsNullOrWhiteSpace(device.CpuName) ? T("Status.Unknown") : device.CpuName,
            gpuText,
            memoryText);
    }

    private void SetSelectedLanguageByCode(string languageCode)
    {
        _isUpdatingLanguageSelection = true;
        try
        {
            SelectedLanguage = Languages.FirstOrDefault(language => string.Equals(language.Code, languageCode, StringComparison.OrdinalIgnoreCase)) ??
                               Languages.FirstOrDefault();
        }
        finally
        {
            _isUpdatingLanguageSelection = false;
        }
    }

    private void CycleLanguage()
    {
        if (Languages.Count == 0)
        {
            return;
        }

        var currentIndex = SelectedLanguage is null ? -1 : Languages.IndexOf(SelectedLanguage);
        var nextIndex = (currentIndex + 1) % Languages.Count;
        SelectedLanguage = Languages[nextIndex];
    }

    private string GetClassificationLabel(ProcessClassification classification) =>
        T($"Enum.ProcessClassification.{classification}");

    private string GetPriorityLabel(PriorityLevel priority) =>
        T($"Enum.PriorityLevel.{priority}");

    private string GetPowerSourceLabel(PowerSourceMode powerSourceMode) =>
        T($"Enum.PowerSourceMode.{powerSourceMode}");

    private string GetGpuModeLabel(GpuPreferenceMode mode) =>
        T($"Enum.GpuPreferenceMode.{mode}");

    private string GetGpuVendorLabel(GpuVendor vendor) =>
        T($"Enum.GpuVendor.{vendor}");

    private string GetCommunitySourceLabel(CommunityProfileSource source) =>
        T($"Enum.CommunityProfileSource.{source}");

    private string GetCommunitySourceFilterLabel(CommunitySourceFilter sourceFilter) =>
        T($"Enum.CommunitySourceFilter.{sourceFilter}");

    private void UpdateMonitoringInteractionState()
    {
        if (_isDisposed)
        {
            return;
        }

        OnPropertyChanged(nameof(IsMonitoringRunning));
        OnPropertyChanged(nameof(CanScanNow));
        OnPropertyChanged(nameof(CanToggleMonitoring));
        OnPropertyChanged(nameof(MonitoringToggleButtonText));
        OnPropertyChanged(nameof(MonitoringModeSummary));

        if (RefreshCommand is AsyncCommand refreshCommand)
        {
            refreshCommand.RaiseCanExecuteChanged();
        }

        if (ToggleMonitoringCommand is AsyncCommand toggleMonitoringCommand)
        {
            toggleMonitoringCommand.RaiseCanExecuteChanged();
        }

        if (ResumeSchedulingCommand is RelayCommand resumeSchedulingCommand)
        {
            resumeSchedulingCommand.RaiseCanExecuteChanged();
        }

        if (ResolveConfigurationRecoveryCommand is AsyncCommand resolveConfigurationRecoveryCommand)
        {
            resolveConfigurationRecoveryCommand.RaiseCanExecuteChanged();
        }
    }

    private static bool ShouldPublishMonitoringNotice(string key) =>
        key is
            "Status.StartupUnsafeExitDetected" or
            "Status.StartupRecoveryApplied" or
            "Status.StartupRecoveryRollbackFailed" or
            "Status.StartupRecoveryRollbackPending" or
            "Status.StartupRecoveryStateReadFailed" or
            "Status.StartupRecoveryStateWriteFailed" or
            "Status.StartupRecoveryFailureStateReadFailed" or
            "Status.StartupRecoveryDisabledProfile" or
            "Status.StartupRecoveryAppliedAndDisabled" or
            "Status.StartupRecoveryProfileDisableFailed" or
            "Status.ScanBlockedWhileSuspended" or
            "Status.MonitorBlockedWhileSuspended" or
            "Status.ResumeBlockedByStartupRecovery" or
            "Status.ResumeBlockedByConfigurationRecovery" or
            "Status.ConfigurationImportRollbackIncomplete" or
            "Status.ConfigurationRecoveryRequired" or
            "Status.ConfigurationRecoveryStillRequired" or
            "Status.ConfigurationRecoveryValidationFailed" or
            "Status.ConfigurationRecoveryClearFailed" or
            "Status.ConfigurationRecoveryResolved" or
            "Status.SummaryRuntimeStatePersistenceFailed" or
            "Status.SummaryRuntimeStatePersistenceRecoveryPending" or
            "Status.CommandFailed" or
            "Status.ScanFailed" or
            "Status.MonitoringFailed" or
            "Status.RollbackApplied" or
            "Status.RollbackIncomplete" or
            "Status.SchedulingResumed";

    private static string GetStatusCenterLevel(string key) =>
        key switch
        {
            "Status.StartupUnsafeExitDetected" => "Warning",
            "Status.StartupRecoveryApplied" => "Warning",
            "Status.StartupRecoveryRollbackFailed" => "Error",
            "Status.StartupRecoveryRollbackPending" => "Error",
            "Status.StartupRecoveryStateReadFailed" => "Error",
            "Status.StartupRecoveryStateWriteFailed" => "Error",
            "Status.StartupRecoveryFailureStateReadFailed" => "Error",
            "Status.StartupRecoveryDisabledProfile" => "Warning",
            "Status.StartupRecoveryAppliedAndDisabled" => "Error",
            "Status.StartupRecoveryProfileDisableFailed" => "Error",
            "Status.ProfileHasValidationIssues" => "Warning",
            "Status.ProfileRollbackFailed" => "Error",
            "Status.CommunityCatalogEmpty" => "Warning",
            "Status.SelectCommunityProfile" => "Warning",
            "Status.SelectCommunityDeviceStrategy" => "Warning",
            "Status.DeviceStrategyPackageEmpty" => "Warning",
            "Status.DeviceStrategyImportCancelled" => "Warning",
            "Status.ScanBlockedWhileSuspended" => "Warning",
            "Status.MonitorBlockedWhileSuspended" => "Warning",
            "Status.ResumeBlockedByStartupRecovery" => "Warning",
            "Status.ResumeBlockedByConfigurationRecovery" => "Error",
            "Status.ConfigurationImportRollbackIncomplete" => "Error",
            "Status.ConfigurationRecoveryRequired" => "Error",
            "Status.ConfigurationRecoveryStillRequired" => "Warning",
            "Status.ConfigurationRecoveryValidationFailed" => "Warning",
            "Status.ConfigurationRecoveryClearFailed" => "Error",
            "Status.ConfigurationRecoveryResolved" => "Success",
            "Status.SummaryRuntimeStatePersistenceFailed" => "Error",
            "Status.SummaryRuntimeStatePersistenceRecoveryPending" => "Error",
            "Status.SettingsSaveFailed" => "Warning",
            "Status.CommandFailed" => "Error",
            "Status.ScanFailed" => "Error",
            "Status.MonitoringFailed" => "Error",
            "Status.SummaryAppliedProfile" => "Success",
            "Status.SummaryAppliedProfileWithBackground" => "Success",
            "Status.SummaryPartiallyAppliedProfile" => "Warning",
            "Status.SummaryPartiallyAppliedProfileWithBackground" => "Warning",
            "Status.RollbackApplied" => "Success",
            "Status.RollbackIncomplete" => "Error",
            "Status.SchedulingResumed" => "Success",
            "Status.SavedProfile" => "Success",
            "Status.ImportedProfiles" => "Success",
            "Status.ExportedProfiles" => "Success",
            "Status.ImportedCommunityProfile" => "Success",
            "Status.ImportedCommunityDeviceStrategy" => "Success",
            "Status.RolledBackProfile" => "Success",
            _ => "Info"
        };

    private static string GetStatusCenterLevel(AppLogLevel level) =>
        level switch
        {
            AppLogLevel.Warning => "Warning",
            AppLogLevel.Error => "Error",
            _ => "Info"
        };

    private string GetCategoryLabel(string category) =>
        T($"Category.{category.Trim().ToLowerInvariant()}");

    private string BuildProfileSummaryTag(PerformanceProfile profile)
    {
        var baseLabel = profile.IsGlobalDefault
            ? T("Status.GlobalDefaultProfileTag")
            : GetClassificationLabel(profile.TargetClassification);

        if (profile.PowerSourceMode == PowerSourceMode.Any)
        {
            return baseLabel;
        }

        return $"{baseLabel} | {GetPowerSourceLabel(profile.PowerSourceMode)}";
    }

    private string T(string key) => _localizationService.Get(key);

    private string F(string key, params object[] args) => _localizationService.Format(key, args);

    private string FormatByKey(string key, params object[] args) => args.Length == 0 ? T(key) : F(key, args);

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        if (_isDisposed)
        {
            return;
        }

        SetSelectedLanguageByCode(_localizationService.CurrentLanguageCode);
        RebuildLocalizedViewState();
    }

    private void OnProfileDraftErrorsChanged(object? sender, System.ComponentModel.DataErrorsChangedEventArgs e)
    {
        if (!_isDisposed)
        {
            ApplyValidationIssues(ProfileDraft.GetValidationMessages());
        }
    }

    private void OnEntryWritten(object? sender, LogEntry entry)
    {
        if (_isDisposed)
        {
            return;
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
        {
            return;
        }

        if (dispatcher.CheckAccess())
        {
            if (!_isDisposed)
            {
                InsertEntry(entry);
            }

            return;
        }

        try
        {
            dispatcher.Invoke(() =>
            {
                if (!_isDisposed)
                {
                    InsertEntry(entry);
                }
            });
        }
        catch (InvalidOperationException)
        {
            // The UI dispatcher may finish shutting down between the state
            // check and Invoke; the log entry is already persisted elsewhere.
        }
    }

    private void InsertEntry(LogEntry entry)
    {
        RecentLogs.Insert(0, CreateLogEntryItem(entry));
        while (RecentLogs.Count > 120)
        {
            RecentLogs.RemoveAt(RecentLogs.Count - 1);
        }

        PublishUserLogEvent(entry);
    }

    private LogEntryItemViewModel CreateLogEntryItem(LogEntry entry)
    {
        var hasLocalizedUserMessage = entry.Audience == LogAudience.User &&
                                      entry.EventCode != UserEventCode.None;
        return new LogEntryItemViewModel
        {
            Entry = entry,
            Level = T($"Enum.AppLogLevel.{entry.Level}"),
            Audience = T($"Enum.LogAudience.{entry.Audience}"),
            Message = hasLocalizedUserMessage
                ? FormatByKey($"UserEvent.{entry.EventCode}", entry.Arguments?.ToArray() ?? [])
                : entry.Message
        };
    }

    private void RelocalizeRecentLogs()
    {
        var entries = RecentLogs.Select(item => item.Entry).ToArray();
        RecentLogs.Clear();
        foreach (var entry in entries)
        {
            RecentLogs.Add(CreateLogEntryItem(entry));
        }
    }

    private void PublishUserLogEvent(LogEntry entry)
    {
        if (entry.Audience != LogAudience.User || entry.EventCode == UserEventCode.None)
        {
            return;
        }

        var messageKey = $"UserEvent.{entry.EventCode}";
        var args = entry.Arguments?.ToArray() ?? [];
        var duplicateCutoff = entry.Timestamp - TimeSpan.FromSeconds(10);
        if (_statusCenterEvents.Any(item =>
                item.Timestamp >= duplicateCutoff &&
                string.Equals(item.MessageKey, messageKey, StringComparison.Ordinal) &&
                item.Args.Select(static value => value?.ToString())
                    .SequenceEqual(args.Select(static value => value?.ToString()))))
        {
            return;
        }

        AddStatusCenterEvent(
            "Section.RuntimeStatus",
            messageKey,
            args,
            GetStatusCenterLevel(entry.Level),
            entry.Timestamp);
    }

    private sealed record StatusCenterEvent(
        DateTimeOffset Timestamp,
        string SourceKey,
        string MessageKey,
        object[] Args,
        string Level);

    private sealed record AppearanceThemeDefinition(
        string Key,
        string DisplayNameKey,
        Color Accent,
        Color AccentSoft,
        Color Border,
        Color Sidebar);
}
