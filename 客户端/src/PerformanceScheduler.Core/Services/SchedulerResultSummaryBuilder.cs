using PerformanceScheduler.Core.Models;

namespace PerformanceScheduler.Core.Services;

internal static class SchedulerResultSummaryBuilder
{
    public static string Build(
        FocusedAppContext app,
        ProfileMatchResult match,
        IReadOnlyList<BackgroundProcessAdjustmentResult> backgroundAdjustments,
        CapabilitySnapshot capabilities,
        PriorityAdjustmentResult? priorityResult)
    {
        if (!capabilities.SupportsRollback)
        {
            return $"Matched profile {match.Profile.Name} for {app.ProcessName}, but adjustments were blocked because rollback is unavailable.";
        }

        var powerAdjustmentUnsupported = !capabilities.SupportsPowerPlanSwitching &&
                                         (match.Profile.PowerPlan.SchemeGuid is not null ||
                                          match.Profile.PowerPlan.Advanced.HasChanges);
        var backgroundAdjustmentUnsupported = backgroundAdjustments.Any(
            adjustment => adjustment.Status == SchedulerActionStatus.Unsupported);
        if (powerAdjustmentUnsupported ||
            priorityResult?.Status == SchedulerActionStatus.Unsupported ||
            backgroundAdjustmentUnsupported)
        {
            return $"Applied supported parts of profile {match.Profile.Name} to {app.ProcessName}; unsupported adjustments were skipped.";
        }

        var appliedCount = backgroundAdjustments.Count(result => result.Status == SchedulerActionStatus.Applied);
        return appliedCount == 0
            ? $"Applied profile {match.Profile.Name} to {app.ProcessName}."
            : $"Applied profile {match.Profile.Name} to {app.ProcessName} and adjusted {appliedCount} background process(es).";
    }

    public static PriorityAdjustmentResult CreateUnsupportedPriorityResult(int processId, string message) => new()
    {
        ProcessId = processId,
        Status = SchedulerActionStatus.Unsupported,
        Message = message
    };

    public static string BuildFailure(
        FocusedAppContext app,
        ProfileMatchResult match,
        IReadOnlyList<SchedulerActionResult> failedActions,
        bool recoveryCompleted)
    {
        var recoverySummary = recoveryCompleted
            ? "Any partially applied changes were rolled back; the profile can be retried."
            : "Rollback is incomplete, so further adjustments are blocked until recovery succeeds.";
        return $"Profile {match.Profile.Name} failed for {app.ProcessName}: " +
               $"{failedActions.Count} action(s) failed. {recoverySummary}";
    }
}
