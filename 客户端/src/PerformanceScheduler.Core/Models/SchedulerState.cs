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

    public IReadOnlyDictionary<int, long> OriginalCpuAffinities { get; init; } =
        new Dictionary<int, long>();

    public IReadOnlyDictionary<int, long> AppliedCpuAffinities { get; init; } =
        new Dictionary<int, long>();

    public IReadOnlyDictionary<int, IReadOnlyList<uint>> OriginalCpuSets { get; init; } =
        new Dictionary<int, IReadOnlyList<uint>>();

    public IReadOnlyDictionary<int, IReadOnlyList<uint>> AppliedCpuSets { get; init; } =
        new Dictionary<int, IReadOnlyList<uint>>();

    public IReadOnlyDictionary<int, IoPriorityLevel> OriginalIoPriorities { get; init; } =
        new Dictionary<int, IoPriorityLevel>();

    public IReadOnlyDictionary<int, IoPriorityLevel> AppliedIoPriorities { get; init; } =
        new Dictionary<int, IoPriorityLevel>();

    public IReadOnlyDictionary<int, MemoryPriorityLevel> OriginalMemoryPriorities { get; init; } =
        new Dictionary<int, MemoryPriorityLevel>();

    public IReadOnlyDictionary<int, MemoryPriorityLevel> AppliedMemoryPriorities { get; init; } =
        new Dictionary<int, MemoryPriorityLevel>();

    public IReadOnlyDictionary<int, JobObjectLimitSnapshot> AppliedJobObjectLimits { get; init; } =
        new Dictionary<int, JobObjectLimitSnapshot>();
}
