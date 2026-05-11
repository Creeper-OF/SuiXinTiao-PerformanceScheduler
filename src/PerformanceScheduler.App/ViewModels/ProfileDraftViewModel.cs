using System.IO;
using System.Collections.ObjectModel;
using System.Collections;
using System.Collections.Specialized;
using System.ComponentModel;
using PerformanceScheduler.Core.Models;

namespace PerformanceScheduler.App.ViewModels;

public sealed class ProfileDraftViewModel : ObservableObject, INotifyDataErrorInfo
{
    private string _id = Guid.NewGuid().ToString("N");
    private string _name = "New Profile";
    private int _version = 1;
    private bool _isEnabled = true;
    private bool _isGlobalDefault;
    private PowerSourceMode _powerSourceMode = PowerSourceMode.Any;
    private ProcessClassification _targetClassification = ProcessClassification.Unknown;
    private string _executableNamesText = string.Empty;
    private string _processNamesText = string.Empty;
    private string _windowTitleContainsText = string.Empty;
    private PriorityLevel _foregroundPriority = PriorityLevel.AboveNormal;
    private bool _restoreOnExit = true;
    private string _notes = string.Empty;
    private GpuPreferenceMode _gpuMode = GpuPreferenceMode.DriverDefault;
    private string _gpuMaxCoreClockMHzText = string.Empty;
    private string _gpuVoltageOffsetMvText = string.Empty;
    private bool _gpuAllowVendorApi;
    private string _processorMaxStatePercentText = string.Empty;
    private PowerPlanOptionViewModel? _selectedPowerPlan;
    private readonly Dictionary<string, List<string>> _errors = new(StringComparer.Ordinal);
    private Func<string, string> _translate = static key => key;
    private Func<string, object[], string> _format = static (key, args) => $"{key}:{string.Join(", ", args)}";
    private bool _hasBackgroundPolicyIssues;
    private bool _hasMatchRuleIssues;
    private bool _hasGpuIssues;
    private bool _hasPowerPlanIssues;

    public ProfileDraftViewModel()
    {
        BackgroundPolicies.CollectionChanged += OnBackgroundPoliciesChanged;
    }

    public ObservableCollection<BackgroundPolicyDraftItemViewModel> BackgroundPolicies { get; } = new();

    public event EventHandler<DataErrorsChangedEventArgs>? ErrorsChanged;

    public bool HasErrors => _errors.Count > 0;

    public bool HasBackgroundPolicyIssues
    {
        get => _hasBackgroundPolicyIssues;
        private set => SetProperty(ref _hasBackgroundPolicyIssues, value);
    }

    public bool HasMatchRuleIssues
    {
        get => _hasMatchRuleIssues;
        private set => SetProperty(ref _hasMatchRuleIssues, value);
    }

    public bool HasGpuIssues
    {
        get => _hasGpuIssues;
        private set => SetProperty(ref _hasGpuIssues, value);
    }

    public bool HasPowerPlanIssues
    {
        get => _hasPowerPlanIssues;
        private set => SetProperty(ref _hasPowerPlanIssues, value);
    }

    public string Id
    {
        get => _id;
        set
        {
            if (SetProperty(ref _id, value))
            {
                RefreshValidation();
            }
        }
    }

    public string Name
    {
        get => _name;
        set
        {
            if (SetProperty(ref _name, value))
            {
                RefreshValidation();
            }
        }
    }

    public int Version
    {
        get => _version;
        set
        {
            if (SetProperty(ref _version, value))
            {
                RefreshValidation();
            }
        }
    }

    public bool IsEnabled
    {
        get => _isEnabled;
        set => SetProperty(ref _isEnabled, value);
    }

    public bool IsGlobalDefault
    {
        get => _isGlobalDefault;
        set
        {
            if (SetProperty(ref _isGlobalDefault, value))
            {
                RefreshValidation();
            }
        }
    }

    public PowerSourceMode PowerSourceMode
    {
        get => _powerSourceMode;
        set => SetProperty(ref _powerSourceMode, value);
    }

    public ProcessClassification TargetClassification
    {
        get => _targetClassification;
        set
        {
            if (SetProperty(ref _targetClassification, value))
            {
                RefreshValidation();
            }
        }
    }

    public string ExecutableNamesText
    {
        get => _executableNamesText;
        set
        {
            if (SetProperty(ref _executableNamesText, value))
            {
                RefreshValidation();
            }
        }
    }

    public string ProcessNamesText
    {
        get => _processNamesText;
        set
        {
            if (SetProperty(ref _processNamesText, value))
            {
                RefreshValidation();
            }
        }
    }

    public string WindowTitleContainsText
    {
        get => _windowTitleContainsText;
        set
        {
            if (SetProperty(ref _windowTitleContainsText, value))
            {
                RefreshValidation();
            }
        }
    }

    public PriorityLevel ForegroundPriority
    {
        get => _foregroundPriority;
        set => SetProperty(ref _foregroundPriority, value);
    }

    public bool RestoreOnExit
    {
        get => _restoreOnExit;
        set => SetProperty(ref _restoreOnExit, value);
    }

    public string Notes
    {
        get => _notes;
        set => SetProperty(ref _notes, value);
    }

    public GpuPreferenceMode GpuMode
    {
        get => _gpuMode;
        set => SetProperty(ref _gpuMode, value);
    }

    public string GpuMaxCoreClockMHzText
    {
        get => _gpuMaxCoreClockMHzText;
        set
        {
            if (SetProperty(ref _gpuMaxCoreClockMHzText, value))
            {
                RefreshValidation();
            }
        }
    }

    public string GpuVoltageOffsetMvText
    {
        get => _gpuVoltageOffsetMvText;
        set
        {
            if (SetProperty(ref _gpuVoltageOffsetMvText, value))
            {
                RefreshValidation();
            }
        }
    }

    public bool GpuAllowVendorApi
    {
        get => _gpuAllowVendorApi;
        set => SetProperty(ref _gpuAllowVendorApi, value);
    }

    public string ProcessorMaxStatePercentText
    {
        get => _processorMaxStatePercentText;
        set
        {
            if (SetProperty(ref _processorMaxStatePercentText, value))
            {
                RefreshValidation();
            }
        }
    }

    public PowerPlanOptionViewModel? SelectedPowerPlan
    {
        get => _selectedPowerPlan;
        set => SetProperty(ref _selectedPowerPlan, value);
    }

    public void ConfigureValidation(Func<string, string> translate, Func<string, object[], string> format)
    {
        _translate = translate;
        _format = format;
        RefreshValidation();
    }

    public void LoadFromProfile(PerformanceProfile profile, IEnumerable<PowerPlanOptionViewModel> powerPlans)
    {
        Id = string.IsNullOrWhiteSpace(profile.Id) ? Guid.NewGuid().ToString("N") : profile.Id;
        Name = profile.Name;
        Version = profile.Version;
        IsEnabled = profile.IsEnabled;
        IsGlobalDefault = profile.IsGlobalDefault;
        PowerSourceMode = profile.PowerSourceMode;
        TargetClassification = profile.TargetClassification;
        ExecutableNamesText = string.Join(Environment.NewLine, profile.Match.ExecutableNames);
        ProcessNamesText = string.Join(Environment.NewLine, profile.Match.ProcessNames);
        WindowTitleContainsText = string.Join(Environment.NewLine, profile.Match.WindowTitleContains);
        ForegroundPriority = profile.Priority.ForegroundPriority;
        RestoreOnExit = profile.PowerPlan.RestoreOnExit;
        ProcessorMaxStatePercentText = profile.PowerPlan.Advanced.ProcessorMaxStatePercent?.ToString() ?? string.Empty;
        GpuMode = profile.Gpu.Mode;
        GpuMaxCoreClockMHzText = profile.Gpu.MaxCoreClockMHz?.ToString() ?? string.Empty;
        GpuVoltageOffsetMvText = profile.Gpu.VoltageOffsetMv?.ToString() ?? string.Empty;
        GpuAllowVendorApi = profile.Gpu.AllowVendorApi;
        Notes = profile.Notes;
        SelectedPowerPlan = powerPlans.FirstOrDefault(option => option.SchemeGuid == profile.PowerPlan.SchemeGuid) ??
                            powerPlans.FirstOrDefault();

        BackgroundPolicies.Clear();
        foreach (var policy in profile.BackgroundPolicies)
        {
            BackgroundPolicies.Add(BackgroundPolicyDraftItemViewModel.FromModel(policy));
        }

        RefreshValidation();
    }

    public void Reset(IEnumerable<PowerPlanOptionViewModel> powerPlans)
    {
        Id = Guid.NewGuid().ToString("N");
        Name = "New Profile";
        Version = 1;
        IsEnabled = true;
        IsGlobalDefault = false;
        PowerSourceMode = PowerSourceMode.Any;
        TargetClassification = ProcessClassification.Unknown;
        ExecutableNamesText = string.Empty;
        ProcessNamesText = string.Empty;
        WindowTitleContainsText = string.Empty;
        ForegroundPriority = PriorityLevel.AboveNormal;
        RestoreOnExit = true;
        ProcessorMaxStatePercentText = string.Empty;
        GpuMode = GpuPreferenceMode.DriverDefault;
        GpuMaxCoreClockMHzText = string.Empty;
        GpuVoltageOffsetMvText = string.Empty;
        GpuAllowVendorApi = false;
        Notes = string.Empty;
        SelectedPowerPlan = powerPlans.FirstOrDefault();
        BackgroundPolicies.Clear();
        RefreshValidation();
    }

    public PerformanceProfile ToProfile()
    {
        var normalizedId = string.IsNullOrWhiteSpace(Id) ? Guid.NewGuid().ToString("N") : Id.Trim();
        var normalizedName = string.IsNullOrWhiteSpace(Name) ? "New Profile" : Name.Trim();

        return new PerformanceProfile
        {
            Id = normalizedId,
            Name = normalizedName,
            Version = Version < 1 ? 1 : Version,
            IsEnabled = IsEnabled,
            IsGlobalDefault = IsGlobalDefault,
            PowerSourceMode = PowerSourceMode,
            TargetClassification = TargetClassification,
            Match = new ProfileMatchCriteria
            {
                ExecutableNames = ParseLines(ExecutableNamesText),
                ProcessNames = ParseLines(ProcessNamesText),
                WindowTitleContains = ParseLines(WindowTitleContainsText),
                Classifications = TargetClassification == ProcessClassification.Unknown
                    ? Array.Empty<ProcessClassification>()
                    : new[] { TargetClassification }
            },
            PowerPlan = new PowerPlanPreference
            {
                SchemeGuid = SelectedPowerPlan?.SchemeGuid,
                PreferredPlanName = SelectedPowerPlan?.DisplayName ?? string.Empty,
                RestoreOnExit = RestoreOnExit,
                Advanced = new PowerPlanAdvancedPreference
                {
                    ProcessorMaxStatePercent = ParseOptionalInt(ProcessorMaxStatePercentText)
                }
            },
            Priority = new PriorityPreference
            {
                ForegroundPriority = ForegroundPriority
            },
            Gpu = new GpuPreference
            {
                Mode = GpuMode,
                MaxCoreClockMHz = ParseOptionalInt(GpuMaxCoreClockMHzText),
                VoltageOffsetMv = ParseOptionalInt(GpuVoltageOffsetMvText),
                AllowVendorApi = GpuAllowVendorApi
            },
            BackgroundPolicies = BackgroundPolicies.Select(policy => policy.ToModel()).ToArray(),
            Notes = Notes.Trim()
        };
    }

    public IReadOnlyList<string> Validate(
        Func<string, string> translate,
        Func<string, object[], string> format)
    {
        var issues = new List<string>();
        var executableNames = ParseLines(ExecutableNamesText);
        var processNames = ParseLines(ProcessNamesText);
        var titleFragments = ParseLines(WindowTitleContainsText);

        if (string.IsNullOrWhiteSpace(Name))
        {
            issues.Add(translate("Validation.ProfileNameRequired"));
        }

        if (Version < 1)
        {
            issues.Add(translate("Validation.ProfileVersionInvalid"));
        }

        if (!IsGlobalDefault &&
            executableNames.Count == 0 && processNames.Count == 0 && titleFragments.Count == 0 && TargetClassification == ProcessClassification.Unknown)
        {
            issues.Add(translate("Validation.AtLeastOneMatchRule"));
        }

        if (executableNames.Any(name => !name.Contains('.', StringComparison.Ordinal)))
        {
            issues.Add(translate("Validation.ExecutableNameExtension"));
        }

        if (processNames.Any(name => name.Contains(Path.DirectorySeparatorChar) || name.Contains(Path.AltDirectorySeparatorChar)))
        {
            issues.Add(translate("Validation.ProcessNamePathSeparator"));
        }

        if (BackgroundPolicies.Any(policy => string.IsNullOrWhiteSpace(policy.Category)))
        {
            issues.Add(translate("Validation.BackgroundPolicyCategoryRequired"));
        }

        var duplicateCategories = BackgroundPolicies
            .Select(policy => policy.Category.Trim())
            .Where(category => !string.IsNullOrWhiteSpace(category))
            .GroupBy(category => category, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();

        if (duplicateCategories.Length > 0)
        {
            issues.Add(format("Validation.BackgroundPolicyDuplicateCategories", new object[] { string.Join(", ", duplicateCategories) }));
        }

        if (!IsOptionalInt(GpuMaxCoreClockMHzText, static value => value > 0))
        {
            issues.Add(translate("Validation.GpuClockLimitInvalid"));
        }

        if (!IsOptionalInt(GpuVoltageOffsetMvText, static value => Math.Abs(value) <= 500))
        {
            issues.Add(translate("Validation.GpuVoltageOffsetInvalid"));
        }

        if (!IsOptionalInt(ProcessorMaxStatePercentText, static value => value is >= 1 and <= 100))
        {
            issues.Add(translate("Validation.ProcessorMaxStateInvalid"));
        }

        return issues;
    }

    public IReadOnlyList<string> GetValidationMessages() =>
        Validate(_translate, _format);

    public IEnumerable GetErrors(string? propertyName)
    {
        if (string.IsNullOrWhiteSpace(propertyName))
        {
            return _errors.Values.SelectMany(static value => value).ToArray();
        }

        return _errors.TryGetValue(propertyName, out var errors)
            ? errors
            : Array.Empty<string>();
    }

    public void RefreshValidation()
    {
        var nameErrors = new List<string>();
        var versionErrors = new List<string>();
        var executableErrors = new List<string>();
        var processErrors = new List<string>();
        var windowTitleErrors = new List<string>();
        var targetClassificationErrors = new List<string>();
        var backgroundPolicyErrors = new List<string>();
        var gpuErrors = new List<string>();
        var powerPlanErrors = new List<string>();

        var executableNames = ParseLines(ExecutableNamesText);
        var processNames = ParseLines(ProcessNamesText);
        var titleFragments = ParseLines(WindowTitleContainsText);

        if (string.IsNullOrWhiteSpace(Name))
        {
            nameErrors.Add(_translate("Validation.ProfileNameRequired"));
        }

        if (Version < 1)
        {
            versionErrors.Add(_translate("Validation.ProfileVersionInvalid"));
        }

        if (!IsGlobalDefault &&
            executableNames.Count == 0 && processNames.Count == 0 && titleFragments.Count == 0 && TargetClassification == ProcessClassification.Unknown)
        {
            var message = _translate("Validation.AtLeastOneMatchRule");
            executableErrors.Add(message);
            processErrors.Add(message);
            windowTitleErrors.Add(message);
            targetClassificationErrors.Add(message);
        }

        if (executableNames.Any(name => !name.Contains('.', StringComparison.Ordinal)))
        {
            executableErrors.Add(_translate("Validation.ExecutableNameExtension"));
        }

        if (processNames.Any(name => name.Contains(Path.DirectorySeparatorChar) || name.Contains(Path.AltDirectorySeparatorChar)))
        {
            processErrors.Add(_translate("Validation.ProcessNamePathSeparator"));
        }

        if (BackgroundPolicies.Any(policy => string.IsNullOrWhiteSpace(policy.Category)))
        {
            backgroundPolicyErrors.Add(_translate("Validation.BackgroundPolicyCategoryRequired"));
        }

        var duplicateCategories = BackgroundPolicies
            .Select(policy => policy.Category.Trim())
            .Where(category => !string.IsNullOrWhiteSpace(category))
            .GroupBy(category => category, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();

        if (duplicateCategories.Length > 0)
        {
            backgroundPolicyErrors.Add(_format("Validation.BackgroundPolicyDuplicateCategories", new object[] { string.Join(", ", duplicateCategories) }));
        }

        if (!IsOptionalInt(GpuMaxCoreClockMHzText, static value => value > 0))
        {
            gpuErrors.Add(_translate("Validation.GpuClockLimitInvalid"));
        }

        if (!IsOptionalInt(GpuVoltageOffsetMvText, static value => Math.Abs(value) <= 500))
        {
            gpuErrors.Add(_translate("Validation.GpuVoltageOffsetInvalid"));
        }

        if (!IsOptionalInt(ProcessorMaxStatePercentText, static value => value is >= 1 and <= 100))
        {
            powerPlanErrors.Add(_translate("Validation.ProcessorMaxStateInvalid"));
        }

        UpdateErrors(nameof(Name), nameErrors);
        UpdateErrors(nameof(Version), versionErrors);
        UpdateErrors(nameof(ExecutableNamesText), executableErrors);
        UpdateErrors(nameof(ProcessNamesText), processErrors);
        UpdateErrors(nameof(WindowTitleContainsText), windowTitleErrors);
        UpdateErrors(nameof(TargetClassification), targetClassificationErrors);
        UpdateErrors(nameof(BackgroundPolicies), backgroundPolicyErrors);
        UpdateErrors(nameof(GpuMode), gpuErrors);
        UpdateErrors(nameof(GpuMaxCoreClockMHzText), gpuErrors);
        UpdateErrors(nameof(GpuVoltageOffsetMvText), gpuErrors);
        UpdateErrors(nameof(ProcessorMaxStatePercentText), powerPlanErrors);

        HasMatchRuleIssues = executableErrors.Count > 0 || processErrors.Count > 0 || windowTitleErrors.Count > 0 || targetClassificationErrors.Count > 0;
        HasBackgroundPolicyIssues = backgroundPolicyErrors.Count > 0;
        HasGpuIssues = gpuErrors.Count > 0;
        HasPowerPlanIssues = powerPlanErrors.Count > 0;
    }

    private static IReadOnlyList<string> ParseLines(string rawText) =>
        rawText
            .Split(new[] { '\r', '\n', ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static int? ParseOptionalInt(string rawValue) =>
        int.TryParse(rawValue, out var value) ? value : null;

    private static bool IsOptionalInt(string rawValue, Func<int, bool> predicate)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return true;
        }

        return int.TryParse(rawValue, out var parsed) && predicate(parsed);
    }

    private void UpdateErrors(string propertyName, IReadOnlyList<string> errors)
    {
        var hasExisting = _errors.ContainsKey(propertyName);
        if (errors.Count == 0)
        {
            if (hasExisting)
            {
                _errors.Remove(propertyName);
                ErrorsChanged?.Invoke(this, new DataErrorsChangedEventArgs(propertyName));
            }

            return;
        }

        _errors[propertyName] = errors.Distinct(StringComparer.Ordinal).ToList();
        ErrorsChanged?.Invoke(this, new DataErrorsChangedEventArgs(propertyName));
    }

    private void OnBackgroundPoliciesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
        {
            foreach (BackgroundPolicyDraftItemViewModel item in e.OldItems)
            {
                item.PropertyChanged -= OnBackgroundPolicyItemPropertyChanged;
            }
        }

        if (e.NewItems is not null)
        {
            foreach (BackgroundPolicyDraftItemViewModel item in e.NewItems)
            {
                item.PropertyChanged += OnBackgroundPolicyItemPropertyChanged;
            }
        }

        RefreshValidation();
    }

    private void OnBackgroundPolicyItemPropertyChanged(object? sender, PropertyChangedEventArgs e) =>
        RefreshValidation();
}
