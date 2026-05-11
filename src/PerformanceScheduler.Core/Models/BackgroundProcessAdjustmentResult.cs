namespace PerformanceScheduler.Core.Models;

public sealed record BackgroundProcessAdjustmentResult
{
    public int ProcessId { get; init; }

    public string ProcessName { get; init; } = string.Empty;

    public string Category { get; init; } = string.Empty;

    public PriorityLevel? PreviousPriority { get; init; }

    public PriorityLevel? AppliedPriority { get; init; }

    public SchedulerActionStatus Status { get; init; } = SchedulerActionStatus.Skipped;

    public string Message { get; init; } = string.Empty;
}
