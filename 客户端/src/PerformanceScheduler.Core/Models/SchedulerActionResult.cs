namespace PerformanceScheduler.Core.Models;

public sealed record SchedulerActionResult
{
    public SchedulerActionPlan Plan { get; init; } = new();

    public SchedulerActionStatus Status { get; init; } = SchedulerActionStatus.Skipped;

    public SchedulerActionReasonCode ReasonCode { get; init; }

    public string Message { get; init; } = string.Empty;

    public DateTimeOffset CompletedAt { get; init; } = DateTimeOffset.UtcNow;
}
