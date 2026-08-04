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
}
