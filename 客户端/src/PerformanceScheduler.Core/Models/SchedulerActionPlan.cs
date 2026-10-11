namespace PerformanceScheduler.Core.Models;

public sealed record SchedulerActionPlan
{
    public Guid ActionId { get; init; } = Guid.NewGuid();

    public SchedulerActionKind Kind { get; init; }

    public string Target { get; init; } = string.Empty;

    public string OriginalValue { get; init; } = string.Empty;

    public string TargetValue { get; init; } = string.Empty;

    public string Reason { get; init; } = string.Empty;

    public string RecoveryDescription { get; init; } = string.Empty;

    public bool RequiresRollback { get; init; }
}
