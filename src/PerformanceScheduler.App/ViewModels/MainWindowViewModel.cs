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
using PerformanceScheduler.Core.Models;
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
    private readonly CommunityProfileCatalogFilter _communityProfileCatalogFilter = new();
    private readonly DeviceStrategyComposer _deviceStrategyComposer = new();
    private IReadOnlyList<CommunityProfileEntry> _allCommunityEntries = Array.Empty<CommunityProfileEntry>();
    private IReadOnlyList<CommunityDeviceStrategyEntry> _allCommunityDeviceStrategyEntries = Array.Empty<CommunityDeviceStrategyEntry>();
    private readonly List<StatusCenterEvent> _statusCenterEvents = new();
    private readonly HashSet<string> _reportedConfigurationIssues = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private static readonly AppearanceThemeDefinition[] AppearanceThemes =
    [
        new("cyuBlue", "Appearance.Theme.CyuBlue", Color.FromRgb(0x1F, 0x74, 0xFF), Color.FromRgb(0xEA, 0xF2, 0xFF), Color.FromRgb(0xE2, 0xEA, 0xF5), Color.FromRgb(0xF8, 0xFB, 0xFF)),
        new("mintCore", "Appearance.Theme.MintCore", Color.FromRgb(0x14, 0x93, 0x72), Color.FromRgb(0xE8, 0xF8, 0xF1), Color.FromRgb(0xD8, 0xED, 0xE6), Color.FromRgb(0xF6, 0xFC, 0xF9)),
        new("warmPulse", "Appearance.Theme.WarmPulse", Color.FromRgb(0xD9, 0x7A, 0x24), Color.FromRgb(0xFF, 0xF1, 0xDF), Color.FromRgb(0xF0, 0xDF, 0xC8), Color.FromRgb(0xFF, 0xFB, 0xF5)),
        new("slateFocus", "Appearance.Theme.SlateFocus", Color.FromRgb(0x4E, 0x65, 0xA8), Color.FromRgb(0xEE, 0xF2, 0xFF), Color.FromRgb(0xD8, 0xE0, 0xF1), Color.FromRgb(0xF7, 0xF9, 0xFE))
    ];

    private static readonly string[] AppearanceBackgroundModes = ["unified", "split"];

    private CancellationTokenSource? _monitoringCts;
    private Task? _monitoringLoopTask;
    private bool _foregroundEventRefreshQueued;
    private DateTimeOffset _lastForegroundEventAt = DateTimeOffset.MinValue;
    private SchedulerRunResult? _lastRunResult;
    private SchedulerRunHistoryItemViewModel? _selectedRuntimeHistory;
    private bool _isUpdatingLanguageSelection;
    private string _focusedAppName = string.Empty;
    private string _focusedAppDetails = string.Empty;
    private string _classificationSummary = string.Empty;
    private string _matchedProfileName = string.Empty;
    private string _profileReason = string.Empty;
    private string _activePowerPlanName = string.Empty;
    private string _prioritySummary = string.Empty;
    private string _metricsSummary = string.Empty;
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
        string databasePath)
    {
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
        ChoosePresentMonExecutableCommand = new RelayCommand(_ => ChoosePresentMonExecutable());
        ClearPresentMonExecutableCommand = new RelayCommand(_ => ClearPresentMonExecutable());
        ImportCommunityProfileCommand = new AsyncCommand(ImportSelectedCommunityProfileAsync, onException: HandleCommandException);
        ImportCommunityDeviceStrategyCommand = new AsyncCommand(ImportSelectedCommunityDeviceStrategyAsync, onException: HandleCommandException);
        CreateDeviceStrategyPackageCommand = new AsyncCommand(CreateDeviceStrategyPackageAsync, onException: HandleCommandException);
        AddProfileToDeviceStrategyCommand = new AsyncCommand(AddSelectedProfileToDeviceStrategyAsync, onException: HandleCommandException);
        RemoveProfileFromDeviceStrategyCommand = new AsyncCommand(RemoveSelectedProfileFromDeviceStrategyAsync, onException: HandleCommandException);
        RollbackProfileCommand = new AsyncCommand(RollbackProfileAsync, onException: HandleCommandException);
        ResumeSchedulingCommand = new RelayCommand(_ => ResumeScheduling(), _ => IsSchedulingSuspended);
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

    public ObservableCollection<LanguageOptionViewModel> Languages { get; } = new();

    public ObservableCollection<string> CapabilityLines { get; } = new();

    public ObservableCollection<LogEntryItemViewModel> RecentLogs { get; } = new();

    public ObservableCollection<string> TelemetryObservationLines { get; } = new();

    public ObservableCollection<string> TelemetryHistoryLines { get; } = new();

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

    public ObservableCollection<ValueOptionViewModel<PowerSourceMode>> PowerSourceOptions { get; } = new();

    public ObservableCollection<ValueOptionViewModel<GpuPreferenceMode>> GpuModeOptions { get; } = new();

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

    public ICommand ChoosePresentMonExecutableCommand { get; }

    public ICommand ClearPresentMonExecutableCommand { get; }

    public ICommand ImportCommunityProfileCommand { get; }

    public ICommand ImportCommunityDeviceStrategyCommand { get; }

    public ICommand CreateDeviceStrategyPackageCommand { get; }

    public ICommand AddProfileToDeviceStrategyCommand { get; }

    public ICommand RemoveProfileFromDeviceStrategyCommand { get; }

    public ICommand RollbackProfileCommand { get; }

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

    public LanguageOptionViewModel? SelectedLanguage
    {
        get => _selectedLanguage;
        set
        {
            if (!SetProperty(ref _selectedLanguage, value) || value is null || _isUpdatingLanguageSelection)
            {
                return;
            }

            _localizationService.SetLanguage(value.Code);
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
                _ = LoadProfileHistoryAsync(value.Profile.Id);
                ApplyValidationIssues(ProfileDraft.GetValidationMessages());
            }
            else if (value is null)
            {
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
            if (SetProperty(ref _pauseSchedulingAfterUnsafeExit, value))
            {
                _settingsStore.Update(settings => settings.PauseSchedulingAfterUnsafeExit = value);
            }
        }
    }

    public bool AutoRollbackOnUnsafeExit
    {
        get => _autoRollbackOnUnsafeExit;
        set
        {
            if (SetProperty(ref _autoRollbackOnUnsafeExit, value))
            {
                _settingsStore.Update(settings => settings.AutoRollbackOnUnsafeExit = value);
            }
        }
    }

    public bool DisableProfileAfterRepeatedUnsafeRuns
    {
        get => _disableProfileAfterRepeatedUnsafeRuns;
        set
        {
            if (SetProperty(ref _disableProfileAfterRepeatedUnsafeRuns, value))
            {
                _settingsStore.Update(settings => settings.DisableProfileAfterRepeatedUnsafeRuns = value);
            }
        }
    }

    public int UnsafeRunThreshold
    {
        get => _unsafeRunThreshold;
        set
        {
            var normalized = Math.Max(1, value);
            if (SetProperty(ref _unsafeRunThreshold, normalized))
            {
                _settingsStore.Update(settings => settings.UnsafeRunThreshold = normalized);
            }
        }
    }

    public int MonitoringIntervalSeconds
    {
        get => _monitoringIntervalSeconds;
        set
        {
            var normalized = NormalizeMonitoringInterval(value);
            if (SetProperty(ref _monitoringIntervalSeconds, normalized))
            {
                _settingsStore.Update(settings => settings.MonitoringIntervalSeconds = normalized);
                if (IsMonitoringRunning)
                {
                    SetMonitoringStatus("Status.MonitoringActive", normalized);
                }
            }
        }
    }

    public bool LaunchAtStartup
    {
        get => _launchAtStartup;
        set
        {
            if (SetProperty(ref _launchAtStartup, value))
            {
                _startupRegistrationService.SetEnabled(value);
                _settingsStore.Update(settings => settings.LaunchAtStartup = value);
            }
        }
    }

    public bool AutoStartMonitoringOnLaunch
    {
        get => _autoStartMonitoringOnLaunch;
        set
        {
            if (SetProperty(ref _autoStartMonitoringOnLaunch, value))
            {
                _settingsStore.Update(settings => settings.AutoStartMonitoringOnLaunch = value);
            }
        }
    }

    public bool LanguageTransitionAnimationEnabled
    {
        get => _languageTransitionAnimationEnabled;
        set
        {
            if (SetProperty(ref _languageTransitionAnimationEnabled, value))
            {
                LanguageTransitionCoordinator.IsEnabled = value;
                _settingsStore.Update(settings => settings.LanguageTransitionAnimationEnabled = value);
            }
        }
    }

    public double LanguageTransitionDurationSeconds
    {
        get => _languageTransitionDurationSeconds;
        set
        {
            var normalized = JsonAppSettingsStore.NormalizeLanguageTransitionDuration(value);
            if (SetProperty(ref _languageTransitionDurationSeconds, normalized))
            {
                _settingsStore.Update(settings => settings.LanguageTransitionDurationSeconds = normalized);
            }
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
            if (SetProperty(ref _uiMotionEnabled, value))
            {
                OnPropertyChanged(nameof(UiStaggeredRevealEnabled));
                _settingsStore.Update(settings => settings.UiMotionEnabled = value);
                if (!value)
                {
                    ApplyAppearanceResources();
                }
            }
        }
    }

    public bool UiMotionStaggerEnabled
    {
        get => _uiMotionStaggerEnabled;
        set
        {
            if (SetProperty(ref _uiMotionStaggerEnabled, value))
            {
                OnPropertyChanged(nameof(UiStaggeredRevealEnabled));
                _settingsStore.Update(settings => settings.UiMotionStaggerEnabled = value);
            }
        }
    }

    public bool UiStaggeredRevealEnabled => UiMotionEnabled && UiMotionStaggerEnabled;

    public double UiMotionDurationMilliseconds
    {
        get => _uiMotionDurationMilliseconds;
        set
        {
            var normalized = JsonAppSettingsStore.NormalizeUiMotionDuration(value);
            if (SetProperty(ref _uiMotionDurationMilliseconds, normalized))
            {
                _settingsStore.Update(settings => settings.UiMotionDurationMilliseconds = normalized);
            }
        }
    }

    public bool AppearanceBackgroundImageEnabled
    {
        get => _appearanceBackgroundImageEnabled;
        set
        {
            if (SetProperty(ref _appearanceBackgroundImageEnabled, value))
            {
                _settingsStore.Update(settings => settings.AppearanceBackgroundImageEnabled = value);
                NotifyAppearanceBackgroundPropertiesChanged();
                SetAppearanceStatus(value ? "Status.AppearanceBackgroundEnabled" : "Status.AppearanceBackgroundDisabled");
            }
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
            if (SetProperty(ref _selectedAppearanceBackgroundMode, normalized))
            {
                _settingsStore.Update(settings => settings.AppearanceBackgroundMode = normalized);
                NotifyAppearanceBackgroundPropertiesChanged();
                SetAppearanceStatus(normalized == "split" ? "Status.AppearanceBackgroundModeSplit" : "Status.AppearanceBackgroundModeUnified");
            }
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
            if (SetProperty(ref _appearanceBackgroundImageOpacity, normalized))
            {
                _settingsStore.Update(settings => settings.AppearanceBackgroundImageOpacity = normalized);
            }
        }
    }

    public string SelectedAppearanceThemeKey
    {
        get => _selectedAppearanceThemeKey;
        set
        {
            var normalized = JsonAppSettingsStore.NormalizeAppearanceThemeKey(value);
            if (SetProperty(ref _selectedAppearanceThemeKey, normalized))
            {
                _settingsStore.Update(settings => settings.AppearanceThemeKey = normalized);
                ApplyAppearanceResources();
                SetAppearanceStatus("Status.AppearanceThemeApplied", GetAppearanceThemeDisplayName(normalized));
            }
        }
    }

    public string AppearanceSurfaceColorHex
    {
        get => _appearanceSurfaceColorHex;
        set
        {
            var normalized = JsonAppSettingsStore.NormalizeAppearanceColorHex(value);
            if (SetProperty(ref _appearanceSurfaceColorHex, normalized))
            {
                _settingsStore.Update(settings => settings.AppearanceSurfaceColorHex = normalized);
                ApplyAppearanceResources();
                SetAppearanceStatus(string.IsNullOrWhiteSpace(normalized) ? "Status.AppearanceSurfaceColorReset" : "Status.AppearanceSurfaceColorApplied", normalized);
            }
        }
    }

    public string AppearanceBorderColorHex
    {
        get => _appearanceBorderColorHex;
        set
        {
            var normalized = JsonAppSettingsStore.NormalizeAppearanceColorHex(value);
            if (SetProperty(ref _appearanceBorderColorHex, normalized))
            {
                _settingsStore.Update(settings => settings.AppearanceBorderColorHex = normalized);
                ApplyAppearanceResources();
                SetAppearanceStatus(string.IsNullOrWhiteSpace(normalized) ? "Status.AppearanceBorderColorReset" : "Status.AppearanceBorderColorApplied", normalized);
            }
        }
    }

    public double AppearanceWindowOverlayOpacity
    {
        get => _appearanceWindowOverlayOpacity;
        set
        {
            var normalized = JsonAppSettingsStore.NormalizeAppearanceWindowOverlayOpacity(value);
            if (SetProperty(ref _appearanceWindowOverlayOpacity, normalized))
            {
                _settingsStore.Update(settings => settings.AppearanceWindowOverlayOpacity = normalized);
                ApplyAppearanceResources();
            }
        }
    }

    public double AppearanceCardOpacity
    {
        get => _appearanceCardOpacity;
        set
        {
            var normalized = JsonAppSettingsStore.NormalizeAppearanceCardOpacity(value);
            if (SetProperty(ref _appearanceCardOpacity, normalized))
            {
                _settingsStore.Update(settings => settings.AppearanceCardOpacity = normalized);
                ApplyAppearanceResources();
            }
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
        if (!IsSchedulingSuspended && AutoStartMonitoringOnLaunch)
        {
            await StartMonitoringAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopMonitoringInternalAsync();
        _foregroundChangeNotifier.ForegroundChanged -= OnForegroundChanged;
        _foregroundChangeNotifier.Dispose();
        _refreshGate.Dispose();
        _logger.EntryWritten -= OnEntryWritten;
        _localizationService.LanguageChanged -= OnLanguageChanged;
        ProfileDraft.ErrorsChanged -= OnProfileDraftErrorsChanged;
        Strings.Dispose();
    }

    private async Task RefreshAsync(bool announceResult = true, bool updateMonitoringStatus = true)
    {
        if (IsSchedulingSuspended)
        {
            return;
        }

        await _refreshGate.WaitAsync();
        try
        {
            var result = await _schedulerOrchestrator.EvaluateForegroundAsync();
            if (_trackAppliedProfileAsync is not null)
            {
                var trackedProfile = result.Success || result.FailureRecoveryCompleted == false
                    ? result.MatchResult?.Profile
                    : null;
                await _trackAppliedProfileAsync(trackedProfile, CancellationToken.None);
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
        if (IsSchedulingSuspended)
        {
            SetMonitoringStatus("Status.ScanBlockedWhileSuspended");
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
            ResumeScheduling();
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
        IsSchedulingSuspended = result.SchedulingSuspended;

        if (!result.HasNotice)
        {
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
        if (IsSchedulingSuspended)
        {
            SetMonitoringStatus("Status.MonitorBlockedWhileSuspended");
            return;
        }

        if (_monitoringCts is not null)
        {
            return;
        }

        _monitoringCts = new CancellationTokenSource();
        if (!_foregroundChangeNotifier.Start())
        {
            _logger.Warn("Foreground change event hook could not be started. Falling back to interval monitoring.");
        }

        _monitoringLoopTask = Task.Run(() => MonitorLoopAsync(_monitoringCts.Token));
        UpdateMonitoringInteractionState();
        SetMonitoringStatus("Status.MonitoringActive", MonitoringIntervalSeconds);
        try
        {
            await RefreshAsync();
        }
        catch (Exception exception)
        {
            _logger.Error("Failed to start monitoring.", exception);
            await StopMonitoringInternalAsync();
            SetMonitoringStatus("Status.MonitoringFailed", exception.Message);
        }
    }

    private async Task StopMonitoringAsync()
    {
        await StopMonitoringInternalAsync();
        SetMonitoringStatus("Status.MonitoringStopped");
    }

    private async Task StopMonitoringInternalAsync()
    {
        var monitoringCts = _monitoringCts;
        if (monitoringCts is null)
        {
            return;
        }

        var monitoringLoopTask = _monitoringLoopTask;
        _monitoringCts = null;
        _monitoringLoopTask = null;
        _foregroundChangeNotifier.Stop();
        monitoringCts.Cancel();

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
            monitoringCts.Dispose();
            UpdateMonitoringInteractionState();
        }
    }

    private async Task RestoreAsync()
    {
        await StopMonitoringInternalAsync();
        await _schedulerOrchestrator.RestoreAsync();
        SetMonitoringStatus("Status.RollbackApplied");
        await RefreshCapabilitiesAsync();
        await LoadPowerPlansAsync();
    }

    private void AnnounceForegroundEvaluation(SchedulerRunResult result, bool updateMonitoringStatus)
    {
        if (updateMonitoringStatus)
        {
            var status = GetResultStatus(result);
            SetMonitoringStatus(status.Key, status.Arguments);
        }
    }

    private async Task MonitorLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(MonitoringIntervalSeconds), cancellationToken);
                await Application.Current.Dispatcher.InvokeAsync(() => RefreshAsync()).Task.Unwrap();
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

            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                var monitoringCts = _monitoringCts;
                _monitoringCts = null;
                _monitoringLoopTask = null;
                monitoringCts?.Dispose();
                UpdateMonitoringInteractionState();
                SetMonitoringStatus("Status.MonitoringFailed", exception.Message);
            });
        }
    }

    private void OnForegroundChanged(object? sender, EventArgs e)
    {
        if (!IsMonitoringRunning || IsSchedulingSuspended)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (now - _lastForegroundEventAt < TimeSpan.FromMilliseconds(180))
        {
            return;
        }

        _lastForegroundEventAt = now;
        if (_foregroundEventRefreshQueued)
        {
            return;
        }

        _foregroundEventRefreshQueued = true;
        _ = Application.Current.Dispatcher.InvokeAsync(RefreshFromForegroundEventAsync).Task.Unwrap();
    }

    private async Task RefreshFromForegroundEventAsync()
    {
        try
        {
            if (!IsMonitoringRunning || IsSchedulingSuspended)
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
        finally
        {
            _foregroundEventRefreshQueued = false;
        }
    }

    private async Task RefreshCapabilitiesAsync(CancellationToken cancellationToken = default)
    {
        var capabilities = await _schedulerOrchestrator.GetCapabilitiesAsync(cancellationToken);

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
            CapabilityLines.Add(F("Capability.NoticeFormat", reason));
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
        LastSummary = BuildLocalizedResultSummary(result);
        LastRefreshTime = F("Status.LastScanFormat", result.OccurredAt.LocalDateTime);
    }

    private string BuildLocalizedResultSummary(SchedulerRunResult result)
    {
        var status = GetResultStatus(result);
        return F(status.Key, status.Arguments);
    }

    private (string Key, object[] Arguments) GetResultStatus(SchedulerRunResult result)
    {
        var profileName = result.MatchResult?.Profile.Name ?? T("Status.Unknown");
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
        var revisions = await _profileManager.LoadProfileHistoryAsync(profileId);
        ProfileHistory.Clear();

        foreach (var revision in revisions)
        {
            ProfileHistory.Add(revision);
        }

        SelectedProfileRevision = ProfileHistory.FirstOrDefault();
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
        _monitoringStatusKey = key;
        _monitoringStatusArgs = args;
        ApplyMonitoringStatus();

        if (ShouldPublishMonitoringNotice(key))
        {
            AddStatusCenterEvent("Section.RuntimeStatus", key, args, GetStatusCenterLevel(key));
        }
    }

    private void HandleCommandException(Exception exception)
    {
        _logger.Error("UI command failed.", exception);
        SetMonitoringStatus("Status.CommandFailed", exception.Message);
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
        GpuAdapterSummary = T("Status.GpuAdapterUnavailable");
        GpuCapabilitySummary = F("Status.GpuCapabilitySummaryFormat", T("Common.No"), T("Common.No"), T("Common.No"), T("Common.No"));
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
        _ = RefreshCapabilitiesAsync();
        _ = RefreshDeviceFingerprintAsync();
    }

    private async Task RefreshDeviceFingerprintAsync(CancellationToken cancellationToken = default)
    {
        var device = await _deviceFingerprintProvider.GetCurrentAsync(cancellationToken);
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
        SelectedLanguage = Languages.FirstOrDefault(language => string.Equals(language.Code, languageCode, StringComparison.OrdinalIgnoreCase)) ??
                           Languages.FirstOrDefault();
        _isUpdatingLanguageSelection = false;
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
    }

    private static bool ShouldPublishMonitoringNotice(string key) =>
        key is
            "Status.StartupUnsafeExitDetected" or
            "Status.StartupRecoveryApplied" or
            "Status.StartupRecoveryDisabledProfile" or
            "Status.StartupRecoveryAppliedAndDisabled" or
            "Status.ScanBlockedWhileSuspended" or
            "Status.MonitorBlockedWhileSuspended" or
            "Status.CommandFailed" or
            "Status.ScanFailed" or
            "Status.MonitoringFailed" or
            "Status.RollbackApplied" or
            "Status.SchedulingResumed";

    private static string GetStatusCenterLevel(string key) =>
        key switch
        {
            "Status.StartupUnsafeExitDetected" => "Warning",
            "Status.StartupRecoveryApplied" => "Warning",
            "Status.StartupRecoveryDisabledProfile" => "Warning",
            "Status.StartupRecoveryAppliedAndDisabled" => "Error",
            "Status.ProfileHasValidationIssues" => "Warning",
            "Status.ProfileRollbackFailed" => "Error",
            "Status.CommunityCatalogEmpty" => "Warning",
            "Status.SelectCommunityProfile" => "Warning",
            "Status.SelectCommunityDeviceStrategy" => "Warning",
            "Status.DeviceStrategyPackageEmpty" => "Warning",
            "Status.DeviceStrategyImportCancelled" => "Warning",
            "Status.ScanBlockedWhileSuspended" => "Warning",
            "Status.MonitorBlockedWhileSuspended" => "Warning",
            "Status.CommandFailed" => "Error",
            "Status.ScanFailed" => "Error",
            "Status.MonitoringFailed" => "Error",
            "Status.SummaryAppliedProfile" => "Success",
            "Status.SummaryAppliedProfileWithBackground" => "Success",
            "Status.RollbackApplied" => "Success",
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
        SetSelectedLanguageByCode(_localizationService.CurrentLanguageCode);
        RebuildLocalizedViewState();
    }

    private void OnProfileDraftErrorsChanged(object? sender, System.ComponentModel.DataErrorsChangedEventArgs e) =>
        ApplyValidationIssues(ProfileDraft.GetValidationMessages());

    private void OnEntryWritten(object? sender, LogEntry entry)
    {
        var dispatcher = Application.Current.Dispatcher;
        if (dispatcher.CheckAccess())
        {
            InsertEntry(entry);
            return;
        }

        dispatcher.Invoke(() => InsertEntry(entry));
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
