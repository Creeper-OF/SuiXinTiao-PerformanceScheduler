using PerformanceScheduler.Core.Models;

namespace PerformanceScheduler.Core.Services;

internal static class SchedulerActionPlanFactory
{
    public static SchedulerActionPlan CreatePowerPlanSwitch(
        PerformanceProfile profile,
        PowerPlanInfo? currentPlan,
        Guid targetScheme) =>
        new()
        {
            Kind = SchedulerActionKind.PowerPlanSwitch,
            Target = "Windows power plan",
            OriginalValue = FormatPowerPlan(currentPlan),
            TargetValue = string.IsNullOrWhiteSpace(profile.PowerPlan.PreferredPlanName)
                ? targetScheme.ToString()
                : $"{profile.PowerPlan.PreferredPlanName} ({targetScheme})",
            Reason = $"Profile '{profile.Name}' selected this power plan.",
            RecoveryDescription = currentPlan is null
                ? "No original power plan was captured; the action must not be applied."
                : $"Restore power plan '{currentPlan.Name}' ({currentPlan.SchemeGuid}).",
            RequiresRollback = currentPlan is not null
        };

    public static SchedulerActionPlan CreateAdvancedPowerSettings(
        PerformanceProfile profile,
        PowerPlanAdvancedState? originalState,
        Guid? schemeGuid,
        PowerSourceMode powerSourceMode) =>
        new()
        {
            Kind = SchedulerActionKind.AdvancedPowerSettings,
            Target = schemeGuid is null
                ? $"Advanced power settings ({powerSourceMode})"
                : $"Power plan {schemeGuid} ({powerSourceMode})",
            OriginalValue = FormatAdvancedPowerState(originalState),
            TargetValue = FormatAdvancedPowerPreference(profile.PowerPlan.Advanced),
            Reason = $"Profile '{profile.Name}' selected advanced processor settings.",
            RecoveryDescription = originalState is null
                ? "No original advanced settings were captured; the action must not be applied."
                : $"Restore {FormatAdvancedPowerState(originalState)}.",
            RequiresRollback = originalState is not null
        };

    public static SchedulerActionPlan CreateForegroundPriority(
        PerformanceProfile profile,
        FocusedAppContext app,
        PriorityLevel? originalPriority) =>
        new()
        {
            Kind = SchedulerActionKind.ForegroundPriority,
            Target = $"{app.ProcessName} ({app.ProcessId})",
            OriginalValue = originalPriority?.ToString() ?? "Unknown",
            TargetValue = profile.Priority.ForegroundPriority.ToString(),
            Reason = $"Profile '{profile.Name}' matched the foreground application.",
            RecoveryDescription = originalPriority is null
                ? "No original priority was captured; the action must not be applied."
                : $"Restore process priority to {originalPriority}.",
            RequiresRollback = originalPriority is not null &&
                               originalPriority != profile.Priority.ForegroundPriority
        };

    public static SchedulerActionPlan CreateForegroundCpuAffinity(
        PerformanceProfile profile,
        FocusedAppContext app,
        long? originalAffinity,
        long targetAffinity) =>
        new()
        {
            Kind = SchedulerActionKind.ForegroundCpuAffinity,
            Target = $"{app.ProcessName} ({app.ProcessId})",
            OriginalValue = originalAffinity is null ? "Unknown" : $"0x{originalAffinity.Value:X}",
            TargetValue = $"0x{targetAffinity:X}",
            Reason = $"Profile '{profile.Name}' configured CPU affinity.",
            RecoveryDescription = originalAffinity is null
                ? "No original CPU affinity was captured; the action must not be applied."
                : $"Restore CPU affinity to 0x{originalAffinity.Value:X}.",
            RequiresRollback = originalAffinity is not null && originalAffinity != targetAffinity
        };

    public static SchedulerActionPlan CreateBackgroundCpuAffinity(
        PerformanceProfile profile,
        string processName,
        int processId,
        string category,
        long originalAffinity,
        long targetAffinity) =>
        new()
        {
            Kind = SchedulerActionKind.BackgroundCpuAffinity,
            Target = $"{processName} ({processId})",
            OriginalValue = $"0x{originalAffinity:X}",
            TargetValue = $"0x{targetAffinity:X}",
            Reason = $"Profile '{profile.Name}' selected '{category}' background CPU affinity policy.",
            RecoveryDescription = $"Restore CPU affinity to 0x{originalAffinity:X}.",
            RequiresRollback = true
        };

    public static IReadOnlyList<SchedulerActionPlan> CreateBackgroundPlans(
        PerformanceProfile profile,
        BackgroundProcessPriorityBaseline baseline)
    {
        var plans = new List<SchedulerActionPlan>(2);
        var target = $"{baseline.ProcessName} ({baseline.ProcessId})";
        if (baseline.PreviousPriority != baseline.TargetPriority)
        {
            plans.Add(new SchedulerActionPlan
            {
                Kind = SchedulerActionKind.BackgroundPriority,
                Target = target,
                OriginalValue = baseline.PreviousPriority.ToString(),
                TargetValue = baseline.TargetPriority.ToString(),
                Reason = $"Profile '{profile.Name}' selected the '{baseline.Category}' background policy.",
                RecoveryDescription = $"Restore process priority to {baseline.PreviousPriority}.",
                RequiresRollback = true
            });
        }

        if (baseline.PreviousEfficiencyMode is not null &&
            baseline.PreviousEfficiencyMode.IsEnabled != baseline.TargetEfficiencyMode)
        {
            plans.Add(new SchedulerActionPlan
            {
                Kind = SchedulerActionKind.BackgroundEfficiencyMode,
                Target = target,
                OriginalValue = baseline.PreviousEfficiencyMode.IsEnabled.ToString(),
                TargetValue = baseline.TargetEfficiencyMode.ToString(),
                Reason = $"Profile '{profile.Name}' selected Efficiency Mode for the '{baseline.Category}' background policy.",
                RecoveryDescription = $"Restore Efficiency Mode to {baseline.PreviousEfficiencyMode.IsEnabled}.",
                RequiresRollback = true
            });
        }

        return plans;
    }

    public static SchedulerActionResult Complete(
        SchedulerActionPlan plan,
        SchedulerActionStatus status,
        string message,
        SchedulerActionReasonCode reasonCode = SchedulerActionReasonCode.None) =>
        new()
        {
            Plan = plan,
            Status = status,
            ReasonCode = reasonCode,
            Message = message
        };

    private static string FormatPowerPlan(PowerPlanInfo? plan) =>
        plan is null ? "Unknown" : $"{plan.Name} ({plan.SchemeGuid})";

    private static string FormatAdvancedPowerState(PowerPlanAdvancedState? state) =>
        state?.ProcessorMaxStatePercent is { } maximum
            ? $"Processor maximum state: {maximum}%"
            : "Unknown";

    private static string FormatAdvancedPowerPreference(PowerPlanAdvancedPreference preference) =>
        preference.ProcessorMaxStatePercent is { } maximum
            ? $"Processor maximum state: {maximum}%"
            : "No changes";

    public static SchedulerActionPlan CreateForegroundCpuSet(
        PerformanceProfile profile,
        FocusedAppContext app,
        IReadOnlyList<uint>? originalCpuSets,
        IReadOnlyList<uint> targetCpuSets) =>
        new()
        {
            Kind = SchedulerActionKind.ForegroundCpuSet,
            Target = $"{app.ProcessName} ({app.ProcessId})",
            OriginalValue = originalCpuSets is null ? "Unknown" : (originalCpuSets.Count == 0 ? "Default" : string.Join(",", originalCpuSets)),
            TargetValue = targetCpuSets.Count == 0 ? "Default" : string.Join(",", targetCpuSets),
            Reason = $"Profile '{profile.Name}' configured CPU sets.",
            RecoveryDescription = originalCpuSets is null
                ? "No original CPU sets were captured; the action must not be applied."
                : (originalCpuSets.Count == 0 ? "Restore default CPU sets." : $"Restore CPU sets to {string.Join(",", originalCpuSets)}."),
            RequiresRollback = originalCpuSets is not null && !AreEquivalentCpuSets(originalCpuSets, targetCpuSets)
        };

    public static SchedulerActionPlan CreateBackgroundCpuSet(
        PerformanceProfile profile,
        string processName,
        int processId,
        string category,
        IReadOnlyList<uint>? originalCpuSets,
        IReadOnlyList<uint> targetCpuSets) =>
        new()
        {
            Kind = SchedulerActionKind.BackgroundCpuSet,
            Target = $"{processName} ({processId})",
            OriginalValue = originalCpuSets is null ? "Unknown" : (originalCpuSets.Count == 0 ? "Default" : string.Join(",", originalCpuSets)),
            TargetValue = targetCpuSets.Count == 0 ? "Default" : string.Join(",", targetCpuSets),
            Reason = $"Profile '{profile.Name}' configured background CPU sets for category '{category}'.",
            RecoveryDescription = originalCpuSets is null
                ? "No original CPU sets were captured; the action must not be applied."
                : (originalCpuSets.Count == 0 ? "Restore default CPU sets." : $"Restore CPU sets to {string.Join(",", originalCpuSets)}."),
            RequiresRollback = originalCpuSets is not null && !AreEquivalentCpuSets(originalCpuSets, targetCpuSets)
        };

    public static SchedulerActionPlan CreateForegroundIoPriority(
        PerformanceProfile profile,
        FocusedAppContext app,
        IoPriorityLevel? originalIoPriority,
        IoPriorityLevel targetIoPriority) =>
        new()
        {
            Kind = SchedulerActionKind.ForegroundIoPriority,
            Target = $"{app.ProcessName} ({app.ProcessId})",
            OriginalValue = originalIoPriority?.ToString() ?? "Unknown",
            TargetValue = targetIoPriority.ToString(),
            Reason = $"Profile '{profile.Name}' configured I/O priority.",
            RecoveryDescription = originalIoPriority is null
                ? "No original I/O priority was captured; the action must not be applied."
                : $"Restore I/O priority to {originalIoPriority}.",
            RequiresRollback = originalIoPriority is not null && originalIoPriority != targetIoPriority
        };

    public static SchedulerActionPlan CreateBackgroundIoPriority(
        PerformanceProfile profile,
        string processName,
        int processId,
        string category,
        IoPriorityLevel originalIoPriority,
        IoPriorityLevel targetIoPriority) =>
        new()
        {
            Kind = SchedulerActionKind.BackgroundIoPriority,
            Target = $"{processName} ({processId})",
            OriginalValue = originalIoPriority.ToString(),
            TargetValue = targetIoPriority.ToString(),
            Reason = $"Profile '{profile.Name}' configured background I/O priority for category '{category}'.",
            RecoveryDescription = $"Restore I/O priority to {originalIoPriority}.",
            RequiresRollback = originalIoPriority != targetIoPriority
        };

    public static SchedulerActionPlan CreateForegroundMemoryPriority(
        PerformanceProfile profile,
        FocusedAppContext app,
        MemoryPriorityLevel? originalMemoryPriority,
        MemoryPriorityLevel targetMemoryPriority) =>
        new()
        {
            Kind = SchedulerActionKind.ForegroundMemoryPriority,
            Target = $"{app.ProcessName} ({app.ProcessId})",
            OriginalValue = originalMemoryPriority?.ToString() ?? "Unknown",
            TargetValue = targetMemoryPriority.ToString(),
            Reason = $"Profile '{profile.Name}' configured memory priority.",
            RecoveryDescription = originalMemoryPriority is null
                ? "No original memory priority was captured; the action must not be applied."
                : $"Restore memory priority to {originalMemoryPriority}.",
            RequiresRollback = originalMemoryPriority is not null && originalMemoryPriority != targetMemoryPriority
        };

    public static SchedulerActionPlan CreateBackgroundMemoryPriority(
        PerformanceProfile profile,
        string processName,
        int processId,
        string category,
        MemoryPriorityLevel originalMemoryPriority,
        MemoryPriorityLevel targetMemoryPriority) =>
        new()
        {
            Kind = SchedulerActionKind.BackgroundMemoryPriority,
            Target = $"{processName} ({processId})",
            OriginalValue = originalMemoryPriority.ToString(),
            TargetValue = targetMemoryPriority.ToString(),
            Reason = $"Profile '{profile.Name}' configured background memory priority for category '{category}'.",
            RecoveryDescription = $"Restore memory priority to {originalMemoryPriority}.",
            RequiresRollback = originalMemoryPriority != targetMemoryPriority
        };

    private static bool AreEquivalentCpuSets(IReadOnlyList<uint> first, IReadOnlyList<uint> second)
    {
        if (ReferenceEquals(first, second)) return true;
        if (first.Count != second.Count) return false;
        return new HashSet<uint>(first).SetEquals(second);
    }

    public static SchedulerActionPlan CreateBackgroundPriority(
        PerformanceProfile profile,
        string processName,
        int processId,
        string category,
        PriorityLevel previousPriority,
        PriorityLevel targetPriority,
        bool? previousEfficiencyMode,
        bool targetEfficiencyMode) =>
        new()
        {
            Kind = SchedulerActionKind.BackgroundPriority,
            Target = $"{processName} ({processId})",
            OriginalValue = previousPriority.ToString(),
            TargetValue = targetPriority.ToString(),
            Reason = $"Profile '{profile.Name}' selected the '{category}' background policy.",
            RecoveryDescription = $"Restore process priority to {previousPriority}.",
            RequiresRollback = previousPriority != targetPriority
        };

    public static SchedulerActionPlan CreateBackgroundJobObjectLimit(
        PerformanceProfile profile,
        string processName,
        int processId,
        string category,
        JobObjectLimitSnapshot? originalLimits,
        JobObjectLimitPreference targetLimits) =>
        new()
        {
            Kind = SchedulerActionKind.BackgroundJobObjectLimit,
            Target = $"{processName} ({processId})",
            OriginalValue = FormatJobObjectLimits(originalLimits),
            TargetValue = FormatJobObjectPreference(targetLimits),
            Reason = $"Profile '{profile.Name}' configured background Job Object limits for category '{category}'.",
            RecoveryDescription = "Remove Job Object limits.",
            RequiresRollback = true
        };

    private static string FormatJobObjectLimits(JobObjectLimitSnapshot? snapshot)
    {
        if (snapshot is null || (!snapshot.HasCpuRateControl && !snapshot.HasMemoryLimit))
        {
            return "None";
        }

        var parts = new List<string>(2);
        if (snapshot.HasCpuRateControl && snapshot.CpuRatePercent is { } cpuRate)
        {
            parts.Add($"CPU: {cpuRate:0.##}%");
        }
        if (snapshot.HasMemoryLimit && snapshot.MaxCommitMemoryBytes is { } memBytes)
        {
            parts.Add($"Memory: {memBytes}B");
        }
        return parts.Count > 0 ? string.Join(", ", parts) : "None";
    }

    private static string FormatJobObjectPreference(JobObjectLimitPreference preference)
    {
        var parts = new List<string>(2);
        if (preference.CpuRate.Enabled && preference.CpuRate.MaxCpuRatePercent is { } cpuRate)
        {
            parts.Add($"CPU: {cpuRate:0.##}%");
        }
        if (preference.MemoryLimit.Enabled && preference.MemoryLimit.MaxCommitMemoryBytes is { } memBytes)
        {
            parts.Add($"Memory: {memBytes}B");
        }
        return parts.Count > 0 ? string.Join(", ", parts) : "None";
    }
}
