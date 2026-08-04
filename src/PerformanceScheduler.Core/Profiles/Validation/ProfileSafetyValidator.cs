using PerformanceScheduler.Core.Models;

namespace PerformanceScheduler.Core.Profiles.Validation;

public static class ProfileSafetyValidator
{
    private const int MaxIdentifierLength = 128;
    private const int MaxNameLength = 128;

    public static ProfileSafetyValidationResult Evaluate(PerformanceProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var issues = new List<ProfileSafetyIssue>();

        AddRequiredTextIssues(profile, issues);
        AddMatchIssues(profile, issues);
        AddPriorityIssues(profile, issues);
        AddPowerIssues(profile, issues);
        AddGpuIssues(profile, issues);
        AddBackgroundPolicyIssues(profile, issues);

        return new ProfileSafetyValidationResult { Issues = issues };
    }

    public static ProfileSafetyValidationResult EvaluateLibrary(IEnumerable<PerformanceProfile?> profiles)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        var profileArray = profiles.ToArray();
        var validProfiles = profileArray
            .OfType<PerformanceProfile>()
            .ToArray();
        var issues = validProfiles
            .SelectMany(profile => Evaluate(profile).Issues)
            .ToList();

        if (profileArray.Any(profile => profile is null))
        {
            AddError(issues, "ProfileRequired", "Every profile library entry requires a profile.");
        }

        var duplicateIds = validProfiles
            .Where(profile => !string.IsNullOrWhiteSpace(profile.Id))
            .GroupBy(profile => profile.Id.Trim(), StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();
        foreach (var duplicateId in duplicateIds)
        {
            AddError(issues, "DuplicateProfileId", $"Profile id '{duplicateId}' is duplicated.");
        }

        var enabledGlobalDefaults = validProfiles
            .Where(profile => profile.IsEnabled && profile.IsGlobalDefault)
            .Select(profile => profile.Name)
            .ToArray();
        if (enabledGlobalDefaults.Length > 1)
        {
            AddError(
                issues,
                "MultipleEnabledGlobalDefaults",
                $"Only one enabled global default profile is allowed; found: {string.Join(", ", enabledGlobalDefaults)}.");
        }

        return new ProfileSafetyValidationResult { Issues = issues };
    }

    public static void EnsureCanPersist(PerformanceProfile profile, string? source = null)
    {
        var result = Evaluate(profile);
        ThrowIfInvalid(profile.Name, result, source);
    }

    public static PerformanceProfile PrepareUntrustedImport(
        PerformanceProfile profile,
        string? source = null)
    {
        var result = Evaluate(profile);
        ThrowIfInvalid(profile.Name, result, source);
        return result.Warnings.Count == 0
            ? profile
            : profile with { IsEnabled = false };
    }

    public static void EnsureLibraryCanPersist(
        IEnumerable<PerformanceProfile?> profiles,
        string? source = null)
    {
        var profileArray = profiles.ToArray();
        var result = EvaluateLibrary(profileArray);
        ThrowIfInvalid("profile library", result, source);
    }

    private static void AddRequiredTextIssues(
        PerformanceProfile profile,
        ICollection<ProfileSafetyIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(profile.Id))
        {
            AddError(issues, "ProfileIdRequired", "Profile id is required.");
        }
        else if (profile.Id.Trim().Length > MaxIdentifierLength)
        {
            AddError(issues, "ProfileIdTooLong", $"Profile id cannot exceed {MaxIdentifierLength} characters.");
        }

        if (string.IsNullOrWhiteSpace(profile.Name))
        {
            AddError(issues, "ProfileNameRequired", "Profile name is required.");
        }
        else if (profile.Name.Trim().Length > MaxNameLength)
        {
            AddError(issues, "ProfileNameTooLong", $"Profile name cannot exceed {MaxNameLength} characters.");
        }

        if (profile.Version < 1)
        {
            AddError(issues, "ProfileVersionInvalid", "Profile version must be at least 1.");
        }
    }

    private static void AddMatchIssues(
        PerformanceProfile profile,
        ICollection<ProfileSafetyIssue> issues)
    {
        if (profile.Match is null)
        {
            AddError(issues, "MatchRulesRequired", "Profile match settings are required.");
            return;
        }

        var executableNames = profile.Match.ExecutableNames ?? Array.Empty<string>();
        var processNames = profile.Match.ProcessNames ?? Array.Empty<string>();
        var titleFragments = profile.Match.WindowTitleContains ?? Array.Empty<string>();
        var classifications = profile.Match.Classifications ?? Array.Empty<ProcessClassification>();
        var hasMatchRule = executableNames.Any(IsMeaningful) ||
                           processNames.Any(IsMeaningful) ||
                           titleFragments.Any(IsMeaningful) ||
                           classifications.Any(classification => classification != ProcessClassification.Unknown);
        if (!profile.IsGlobalDefault && !hasMatchRule)
        {
            AddError(issues, "AtLeastOneMatchRule", "A non-global profile requires at least one match rule.");
        }

        if (executableNames.Where(IsMeaningful).Any(name =>
                !name.Contains('.', StringComparison.Ordinal) || ContainsPathSeparator(name)))
        {
            AddError(issues, "ExecutableNameInvalid", "Executable match rules must be file names with extensions, not paths.");
        }

        if (processNames.Where(IsMeaningful).Any(ContainsPathSeparator))
        {
            AddError(issues, "ProcessNameInvalid", "Process name match rules cannot contain path separators.");
        }
    }

    private static void AddPriorityIssues(
        PerformanceProfile profile,
        ICollection<ProfileSafetyIssue> issues)
    {
        if (profile.Priority is null)
        {
            AddError(issues, "PrioritySettingsRequired", "Priority settings are required.");
            return;
        }

        if (!Enum.IsDefined(profile.Priority.ForegroundPriority))
        {
            AddError(issues, "ForegroundPriorityInvalid", "Foreground priority value is invalid.");
        }
        else if (profile.Priority.ForegroundPriority == PriorityLevel.RealTime)
        {
            AddError(issues, "RealTimePriorityBlocked", "RealTime foreground priority is blocked by the safety policy.");
        }
    }

    private static void AddPowerIssues(
        PerformanceProfile profile,
        ICollection<ProfileSafetyIssue> issues)
    {
        if (profile.PowerPlan is null)
        {
            AddError(issues, "PowerSettingsRequired", "Power plan settings are required.");
            return;
        }

        if (profile.PowerPlan.Advanced is null)
        {
            AddError(issues, "AdvancedPowerSettingsRequired", "Advanced power settings are required.");
            return;
        }

        if (profile.PowerPlan.Advanced.ProcessorMaxStatePercent is { } maximumState &&
            maximumState is < 1 or > 100)
        {
            AddError(issues, "ProcessorMaxStateInvalid", "Processor maximum state must be between 1 and 100 percent.");
        }

        if (!profile.PowerPlan.RestoreOnExit &&
            (profile.PowerPlan.SchemeGuid is not null || profile.PowerPlan.Advanced.HasChanges))
        {
            AddWarning(issues, "RestoreOnExitDisabled", "Power changes are configured not to restore when the profile exits.");
        }
    }

    private static void AddGpuIssues(
        PerformanceProfile profile,
        ICollection<ProfileSafetyIssue> issues)
    {
        if (profile.Gpu is null)
        {
            AddError(issues, "GpuSettingsRequired", "GPU settings are required.");
            return;
        }

        if (profile.Gpu.MaxCoreClockMHz is { } maximumClock && maximumClock <= 0)
        {
            AddError(issues, "GpuClockLimitInvalid", "GPU maximum core clock must be greater than zero.");
        }

        if (profile.Gpu.VoltageOffsetMv is { } voltageOffset && Math.Abs((long)voltageOffset) > 500)
        {
            AddError(issues, "GpuVoltageOffsetInvalid", "GPU voltage offset must stay within +/-500 mV.");
        }

        if (profile.Gpu.VoltageOffsetMv is not null)
        {
            AddWarning(issues, "GpuVoltageRequiresApproval", "GPU voltage control is high risk and requires explicit device capability and user approval.");
        }

        if (profile.Gpu.AllowVendorApi)
        {
            AddWarning(issues, "VendorApiRequiresApproval", "Vendor GPU APIs require device capability checks and explicit user approval.");
        }
    }

    private static void AddBackgroundPolicyIssues(
        PerformanceProfile profile,
        ICollection<ProfileSafetyIssue> issues)
    {
        var policies = profile.BackgroundPolicies ?? Array.Empty<BackgroundProcessPolicy>();
        foreach (var policy in policies)
        {
            if (policy is null || string.IsNullOrWhiteSpace(policy.Category))
            {
                AddError(issues, "BackgroundCategoryRequired", "Every background policy requires a category.");
                continue;
            }

            if (!Enum.IsDefined(policy.TargetPriority))
            {
                AddError(issues, "BackgroundPriorityInvalid", $"Background category '{policy.Category}' has an invalid priority.");
            }
            else if (policy.TargetPriority == PriorityLevel.RealTime)
            {
                AddError(issues, "RealTimePriorityBlocked", $"Background category '{policy.Category}' cannot use RealTime priority.");
            }
        }

        var duplicateCategories = policies
            .Where(policy => policy is not null && !string.IsNullOrWhiteSpace(policy.Category))
            .GroupBy(policy => policy.Category.Trim(), StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();
        foreach (var category in duplicateCategories)
        {
            AddError(issues, "DuplicateBackgroundCategory", $"Background category '{category}' is duplicated.");
        }
    }

    private static bool IsMeaningful(string? value) => !string.IsNullOrWhiteSpace(value);

    private static bool ContainsPathSeparator(string value) =>
        value.Contains(Path.DirectorySeparatorChar) || value.Contains(Path.AltDirectorySeparatorChar);

    private static void ThrowIfInvalid(
        string profileName,
        ProfileSafetyValidationResult result,
        string? source)
    {
        if (!result.CanPersist)
        {
            throw new InvalidProfileConfigurationException(profileName, result.Errors, source);
        }
    }

    private static void AddError(ICollection<ProfileSafetyIssue> issues, string code, string message) =>
        issues.Add(new ProfileSafetyIssue(code, ProfileSafetySeverity.Error, message));

    private static void AddWarning(ICollection<ProfileSafetyIssue> issues, string code, string message) =>
        issues.Add(new ProfileSafetyIssue(code, ProfileSafetySeverity.Warning, message));
}
