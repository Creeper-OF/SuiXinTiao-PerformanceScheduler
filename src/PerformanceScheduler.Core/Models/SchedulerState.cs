namespace PerformanceScheduler.Core.Models;

public sealed record SchedulerState
{
    public Guid TransactionId { get; init; }

    public DateTimeOffset CapturedAt { get; init; } = DateTimeOffset.UtcNow;

    public PowerPlanInfo? PreviousPowerPlan { get; init; }

    public PowerPlanInfo? AppliedPowerPlan { get; init; }

    public PowerPlanAdvancedState? PreviousAdvancedPowerSettings { get; init; }

    public PowerPlanAdvancedState? AppliedAdvancedPowerSettings { get; init; }

    public IReadOnlyDictionary<int, PriorityLevel> OriginalPriorities { get; init; } =
        new Dictionary<int, PriorityLevel>();

    public IReadOnlyDictionary<int, PriorityLevel> AppliedPriorities { get; init; } =
        new Dictionary<int, PriorityLevel>();

    public IReadOnlyDictionary<int, ProcessIdentitySnapshot> ProcessIdentities { get; init; } =
        new Dictionary<int, ProcessIdentitySnapshot>();

    public IReadOnlyDictionary<int, ProcessEfficiencyModeState> OriginalEfficiencyModes { get; init; } =
        new Dictionary<int, ProcessEfficiencyModeState>();

    public IReadOnlyDictionary<int, bool> AppliedEfficiencyModes { get; init; } =
        new Dictionary<int, bool>();
}
