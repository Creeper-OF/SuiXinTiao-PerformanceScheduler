namespace PerformanceScheduler.Core.Models;

public sealed record SchedulerActionExecutionResult
{
    public PowerPlanInfo? ActivePowerPlan { get; init; }

    public PriorityAdjustmentResult? PriorityChange { get; init; }

    public IReadOnlyList<BackgroundProcessAdjustmentResult> BackgroundAdjustments { get; init; } =
        Array.Empty<BackgroundProcessAdjustmentResult>();

    public IReadOnlyList<SchedulerActionResult> Actions { get; init; } =
        Array.Empty<SchedulerActionResult>();
}
