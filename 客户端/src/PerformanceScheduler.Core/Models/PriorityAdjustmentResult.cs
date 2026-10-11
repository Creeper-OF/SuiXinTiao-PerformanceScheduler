namespace PerformanceScheduler.Core.Models;

public sealed record PriorityAdjustmentResult
{
    public int ProcessId { get; init; }

    public PriorityLevel? PreviousPriority { get; init; }

    public PriorityLevel? AppliedPriority { get; init; }

    public SchedulerActionStatus Status { get; init; } = SchedulerActionStatus.Skipped;

    public string Message { get; init; } = string.Empty;
}
