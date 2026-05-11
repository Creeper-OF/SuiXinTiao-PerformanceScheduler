namespace PerformanceScheduler.Core.Models;

public sealed record SchedulerState
{
    public DateTimeOffset CapturedAt { get; init; } = DateTimeOffset.UtcNow;

    public PowerPlanInfo? PreviousPowerPlan { get; init; }

    public PowerPlanAdvancedState? PreviousAdvancedPowerSettings { get; init; }

    public IReadOnlyDictionary<int, PriorityLevel> OriginalPriorities { get; init; } =
        new Dictionary<int, PriorityLevel>();
}
