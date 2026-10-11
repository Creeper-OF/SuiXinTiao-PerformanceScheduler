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
        AddTargetExperienceIssues(profile, issues);
        AddBackgroundPolicyIssues(profile, issues);
        AddCpuAffinityIssues(profile, issues);
        AddCpuSetIssues(profile, issues);
        AddMemoryIoPriorityIssues(profile, issues);

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

    public static ProfileSafetyValidationResult EvaluateUntrustedImport(PerformanceProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var result = Evaluate(profile);
        return result with
        {
            Issues = result.Issues
                .Append(new ProfileSafetyIssue(
                    "UntrustedImportSource",
                    ProfileSafetySeverity.Warning,
                    "This profile came from an untrusted file source and requires manual review before enabling.",
                    ProfileSafetyRiskLevel.High))
                .ToArray()
        };
    }

    public static PerformanceProfile PrepareUntrustedImport(
        PerformanceProfile profile,
        string? source = null)
    {
        var result = EvaluateUntrustedImport(profile);
        ThrowIfInvalid(profile.Name, result, source);
        return profile with { IsEnabled = false };
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

        if (profile.Match.ExecutableNames is null)
        {
            AddError(issues, "ExecutableNamesRequired", "Executable match rules cannot be null.");
        }

        if (profile.Match.ProcessNames is null)
        {
            AddError(issues, "ProcessNamesRequired", "Process match rules cannot be null.");
        }

        if (profile.Match.WindowTitleContains is null)
        {
            AddError(issues, "WindowTitleRulesRequired", "Window title match rules cannot be null.");
        }

        if (profile.Match.Classifications is null)
        {
            AddError(issues, "ClassificationsRequired", "Classification match rules cannot be null.");
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

        var matchesProtectedProcess = executableNames
            .Concat(processNames)
            .Where(IsMeaningful)
            .Any(ProtectedProcessNamePolicy.IsProtected);
        if (matchesProtectedProcess)
        {
            AddWarning(
                issues,
                "ProtectedProcessMatchRequiresReview",
                "This profile matches a protected Windows process and will be blocked from priority changes.",
                ProfileSafetyRiskLevel.High);
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
            AddWarning(
                issues,
                "RestoreOnExitDisabled",
                "Power changes are configured not to restore when the profile exits.",
                ProfileSafetyRiskLevel.High);
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
            AddWarning(
                issues,
                "GpuVoltageRequiresApproval",
                "GPU voltage control is high risk and requires explicit device capability and user approval.",
                ProfileSafetyRiskLevel.High);
        }

        if (profile.Gpu.AllowVendorApi)
        {
            AddWarning(
                issues,
                "VendorApiRequiresApproval",
                "Vendor GPU APIs require device capability checks and explicit user approval.",
                ProfileSafetyRiskLevel.High);
        }
    }

    private static void AddBackgroundPolicyIssues(
        PerformanceProfile profile,
        ICollection<ProfileSafetyIssue> issues)
    {
        if (profile.BackgroundPolicies is null)
        {
            AddError(issues, "BackgroundPoliciesRequired", "Background policies cannot be null.");
        }

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

            if (policy.TargetAffinityMask is { } targetAffinity && targetAffinity <= 0)
            {
                AddError(issues, "BackgroundPolicyAffinityMaskInvalid", $"Background category '{policy.Category}' has an invalid CPU affinity mask.");
            }
            if (policy.TargetCpuSets is { Count: 0 })
            {
                AddError(issues, "BackgroundPolicyCpuSetsEmpty", $"Background category '{policy.Category}' has empty CPU sets.");
            }

            if (policy.TargetIoPriority is { } targetIo)
            {
                if (!Enum.IsDefined(targetIo))
                {
                    AddError(issues, "BackgroundPolicyIoPriorityInvalid", $"Background category '{policy.Category}' has an invalid I/O priority.");
                }
                else if (targetIo is IoPriorityLevel.High or IoPriorityLevel.Critical)
                {
                    AddError(issues, "BackgroundPolicyIoPriorityTooHigh", $"Background category '{policy.Category}' cannot use High or Critical I/O priority.");
                }
            }

            if (policy.TargetMemoryPriority is { } targetMem && !Enum.IsDefined(targetMem))
            {
                AddError(issues, "BackgroundPolicyMemoryPriorityInvalid", $"Background category '{policy.Category}' has an invalid memory priority.");
            }

            var effectiveLimits = policy.GetEffectiveJobObjectLimits();
            if (effectiveLimits is not null)
            {
                if (effectiveLimits.CpuRate.Enabled || effectiveLimits.CpuRate.MaxCpuRatePercent is not null)
                {
                    if (effectiveLimits.CpuRate.MaxCpuRatePercent is not { } cpuRate ||
                        !double.IsFinite(cpuRate) ||
                        cpuRate is < 1.0 or > 100.0)
                    {
                        AddError(
                            issues,
                            "BackgroundPolicyJobCpuRateInvalid",
                            $"Background category '{policy.Category}' has an invalid Job Object CPU rate percent. Must be between 1.0 and 100.0.");
                    }
                }

                if (effectiveLimits.MemoryLimit.Enabled || effectiveLimits.MemoryLimit.MaxCommitMemoryBytes is not null)
                {
                    if (effectiveLimits.MemoryLimit.MaxCommitMemoryBytes is not { } memBytes || memBytes == 0)
                    {
                        AddError(
                            issues,
                            "BackgroundPolicyJobMemoryLimitInvalid",
                            $"Background category '{policy.Category}' has an invalid Job Object memory limit. Must be greater than 0.");
                    }
                }
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

    private static void AddTargetExperienceIssues(
        PerformanceProfile profile,
        ICollection<ProfileSafetyIssue> issues)
    {
        var target = profile.TargetExperience;
        if (target is null)
        {
            AddError(issues, "TargetExperienceSettingsRequired", "Target experience settings are required.");
            return;
        }

        if (!Enum.IsDefined(target.Priority))
        {
            AddError(issues, "TargetExperiencePriorityInvalid", "Target experience priority is invalid.");
        }

        if (!Enum.IsDefined(target.ControlMode))
        {
            AddError(issues, "TargetExperienceControlModeInvalid", "Target experience control mode is invalid.");
        }

        if (!Enum.IsDefined(target.NoisePreference))
        {
            AddError(issues, "TargetExperienceNoisePreferenceInvalid", "Target experience noise preference is invalid.");
        }

        if (target.TargetFramesPerSecond is { } framesPerSecond && framesPerSecond is < 1 or > 1000)
        {
            AddError(issues, "TargetFramesPerSecondInvalid", "Target frame rate must be between 1 and 1000 FPS.");
        }

        if (target.MaximumFrameTimeP95Milliseconds is { } frameTime &&
            (!double.IsFinite(frameTime) || frameTime <= 0 || frameTime > 1000))
        {
            AddError(issues, "TargetFrameTimeInvalid", "Target frame time must be between 0 and 1000 milliseconds.");
        }

        if (target.MaximumTemperatureCelsius is { } temperature &&
            (!double.IsFinite(temperature) || temperature <= 0 || temperature > 150))
        {
            AddError(issues, "TargetTemperatureInvalid", "Target temperature must be between 0 and 150 degrees Celsius.");
        }

        if (target.MaximumPowerWatts is { } power &&
            (!double.IsFinite(power) || power <= 0 || power > 5000))
        {
            AddError(issues, "TargetPowerInvalid", "Target power must be between 0 and 5000 watts.");
        }

        if (target.ObservationWindowSeconds is < 1 or > 3600)
        {
            AddError(issues, "TargetObservationWindowInvalid", "Target observation window must be between 1 and 3600 seconds.");
        }

        if (target.MinimumStableSamples is < 1 or > 1000)
        {
            AddError(issues, "TargetStableSamplesInvalid", "Target stable sample count must be between 1 and 1000.");
        }

        if (!double.IsFinite(target.HysteresisPercent) || target.HysteresisPercent is < 0 or > 50)
        {
            AddError(issues, "TargetHysteresisInvalid", "Target hysteresis must be between 0 and 50 percent.");
        }

        if (profile.TargetExperience.Enabled && target.ControlMode == TargetExperienceControlMode.Automatic)
        {
            AddWarning(
                issues,
                "TargetAutomaticModeRequiresVerifiedProvider",
                "Automatic target-experience control remains unavailable until a verified reversible provider is selected.",
                ProfileSafetyRiskLevel.High);
        }
    }

    private static void AddCpuAffinityIssues(
        PerformanceProfile profile,
        ICollection<ProfileSafetyIssue> issues)
    {
        var affinity = profile.CpuAffinity;
        if (affinity is null)
        {
            AddError(issues, "CpuAffinitySettingsRequired", "CPU affinity settings cannot be null.");
            return;
        }

        if (affinity.Enabled)
        {
            if (affinity.ForegroundAffinityMask is { } fgMask && fgMask <= 0)
            {
                AddError(issues, "ForegroundAffinityMaskInvalid", "Foreground CPU affinity mask must be greater than zero.");
            }

            if (affinity.BackgroundAffinityMask is { } bgMask && bgMask <= 0)
            {
                AddError(issues, "BackgroundAffinityMaskInvalid", "Background CPU affinity mask must be greater than zero.");
            }
        }
    }


    private static void AddCpuSetIssues(
        PerformanceProfile profile,
        ICollection<ProfileSafetyIssue> issues)
    {
        var cpuSets = profile.CpuSets;
        if (cpuSets is null)
        {
            AddError(issues, "CpuSetSettingsRequired", "CPU set settings cannot be null.");
            return;
        }

        if (cpuSets.Enabled)
        {
            if (cpuSets.ForegroundCpuSets is { Count: 0 })
            {
                AddError(issues, "ForegroundCpuSetsEmpty", "Foreground CPU sets cannot be empty when configured.");
            }

            if (cpuSets.BackgroundCpuSets is { Count: 0 })
            {
                AddError(issues, "BackgroundCpuSetsEmpty", "Background CPU sets cannot be empty when configured.");
            }
        }
    }

    private static void AddMemoryIoPriorityIssues(
        PerformanceProfile profile,
        ICollection<ProfileSafetyIssue> issues)
    {
        var memoryIo = profile.MemoryIoPriority;
        if (memoryIo is null)
        {
            AddError(issues, "MemoryIoPrioritySettingsRequired", "Memory and I/O priority settings cannot be null.");
            return;
        }

        if (memoryIo.Enabled)
        {
            if (memoryIo.ForegroundIoPriority is { } fgIo && !Enum.IsDefined(fgIo))
            {
                AddError(issues, "ForegroundIoPriorityInvalid", "Foreground I/O priority is invalid.");
            }

            if (memoryIo.BackgroundIoPriority is { } bgIo)
            {
                if (!Enum.IsDefined(bgIo))
                {
                    AddError(issues, "BackgroundIoPriorityInvalid", "Background I/O priority is invalid.");
                }
                else if (bgIo is IoPriorityLevel.High or IoPriorityLevel.Critical)
                {
                    AddError(issues, "BackgroundIoPriorityTooHigh", "Background I/O priority cannot be High or Critical.");
                }
            }

            if (memoryIo.ForegroundMemoryPriority is { } fgMem && !Enum.IsDefined(fgMem))
            {
                AddError(issues, "ForegroundMemoryPriorityInvalid", "Foreground memory priority is invalid.");
            }

            if (memoryIo.BackgroundMemoryPriority is { } bgMem && !Enum.IsDefined(bgMem))
            {
                AddError(issues, "BackgroundMemoryPriorityInvalid", "Background memory priority is invalid.");
            }
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
        issues.Add(new ProfileSafetyIssue(code, ProfileSafetySeverity.Error, message, ProfileSafetyRiskLevel.Blocked));

    private static void AddWarning(
        ICollection<ProfileSafetyIssue> issues,
        string code,
        string message,
        ProfileSafetyRiskLevel riskLevel = ProfileSafetyRiskLevel.Notice) =>
        issues.Add(new ProfileSafetyIssue(code, ProfileSafetySeverity.Warning, message, riskLevel));
}
